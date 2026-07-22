using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the In-Transit Report (Inbound) — containers released from UAE
/// (USA.dbo.ExportPass) that haven't been receipted yet at destination
/// (excluded via bfldata..contreceiptexport), enriched with the goods-issue
/// shop/pallet info and the Division/Department/Brand of everything on the
/// transfer.
///
/// Everything lives on the single OnPremBackup (UAE master) server — country
/// DBs (P2EXPORT, BFLKSA, ...) are sibling catalogs on that same server,
/// reached via 3-part naming with the catalog name resolved dynamically from
/// bfldata.dbo.DataSettings.DataName (same pattern as WhBoxItemsSource).
///
/// A single country's shipments can span more than one DataSettings shop row
/// (e.g. KSA has both EX2KSA→P2EXPORT and BFLJEDREDSEA→BFLKSA), so the base
/// query resolves CostCodeTo/LocCodeTo/DataName per pallet via its ShopIssue
/// rather than assuming one mapping per country.
///
/// Perf notes (measured against the real backup server):
///   - "NOT IN (subquery)" against contreceiptexport took ~7.5s; the
///     equivalent correlated "NOT EXISTS" takes ~0.3s (better anti-join
///     plan) — same result set, so always prefer NOT EXISTS here.
///   - The per-shop vTransferDetail lookup is the dominant remaining cost
///     (~3s per 1000-TrfNo chunk) and a "BFL Group" run can need a dozen-plus
///     chunks, so chunks run concurrently (own connection each, like
///     ContainerReceiptService's warehouse fan-out) instead of sequentially.
///   - vTransferDetail and USAPriority are joined in one query per chunk
///     instead of two round trips (vTransferDetail groupcodes, then a
///     separate USAPriority lookup).
/// </summary>
public class InTransitReportService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;
    private const int TrfNoChunkSize = 1000;
    private const int MaxConcurrentChunkQueries = 8;

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT SIMCountry FROM bfldata..DataSettings
              WHERE SIMCountry NOT IN ('', 'ECOM', 'Ex2Locations', 'UAE')
              ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    public async Task<List<InTransitReportRow>> GetInTransitAsync(
        InTransitReportFilter f, CancellationToken ct = default)
    {
        List<BaseRow> baseRows;
        await using (var conn = OpenOnPremBackup())
        {
            var country = string.IsNullOrWhiteSpace(f.Country) ? null : f.Country;
            baseRows = (await conn.QueryAsync<BaseRow>(new CommandDefinition(
                BaseSql, new { since = f.Since.Date, country },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        if (baseRows.Count == 0) return [];

        var shopGroups = baseRows
            .GroupBy(r => (r.DataName, r.CostCodeTo, r.LocCodeTo))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.DataName))
            .ToList();

        // (Division, Department, Brand, Qty) per TrfNo — one query per (shop, chunk),
        // run concurrently since these are all independent reads.
        using var throttle = new SemaphoreSlim(MaxConcurrentChunkQueries);
        var chunkTasks = new List<Task<List<ChunkRow>>>();
        foreach (var shop in shopGroups)
        {
            var trfNos = shop.Select(r => r.TrfNo).Distinct().ToList();
            foreach (var chunk in Chunk(trfNos, TrfNoChunkSize))
                chunkTasks.Add(RunChunkAsync(shop.Key.DataName!, shop.Key.CostCodeTo, shop.Key.LocCodeTo, chunk, throttle, ct));
        }

        var chunkResults = await Task.WhenAll(chunkTasks);

        var entriesByTrfNo = new Dictionary<string, List<ChunkRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rows in chunkResults)
            foreach (var r in rows)
            {
                if (!entriesByTrfNo.TryGetValue(r.TrfNo, out var list))
                    entriesByTrfNo[r.TrfNo] = list = [];
                list.Add(r);
            }

        const int TopN = 5;
        var result = new List<InTransitReportRow>();
        foreach (var g in baseRows.GroupBy(r => r.GinNo))
        {
            var first = g.First();
            var trfNos = g.Select(r => r.TrfNo).Distinct();

            // Roll qty up to Division/Department/Brand across every transfer of this
            // GIN, then rank each dimension independently by its own total qty.
            var qtyByDivision   = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var qtyByDepartment = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var qtyByBrand      = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in trfNos)
            {
                if (!entriesByTrfNo.TryGetValue(t, out var entries)) continue;
                foreach (var e in entries)
                {
                    Accumulate(qtyByDivision,   e.Division,   e.Qty);
                    Accumulate(qtyByDepartment, e.Department, e.Qty);
                    Accumulate(qtyByBrand,      e.Brand,      e.Qty);
                }
            }

            result.Add(new InTransitReportRow(
                Country:       first.Country,
                GinNo:         first.GinNo,
                ReleasedDate:  first.ReleasedDate,
                EtaDate:       first.EtaDate,
                ShipNo:        first.ShipNo,
                TotalQty:      first.TotalQty,
                TransferCount: first.TransferCount,
                Whouse:        first.Whouse ?? "",
                Remarks:       first.Remarks ?? "",
                Division:      TopByQty(qtyByDivision, TopN),
                Department:    TopByQty(qtyByDepartment, TopN),
                Brand:         TopByQty(qtyByBrand, TopN)));
        }

        return result.OrderBy(r => r.ReleasedDate).ToList();
    }

    private async Task<List<ChunkRow>> RunChunkAsync(
        string dataName, string costCodeTo, string locCodeTo, List<string> trfNos,
        SemaphoreSlim throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            await using var conn = OpenOnPremBackup();
            var sql = $@"
                SELECT vtd.TrfNo, up.DivisionY AS Division, up.Department, up.Brand, SUM(vtd.Quantity) AS Qty
                FROM [{dataName}].dbo.vTransferDetail vtd WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = vtd.groupcode
                WHERE vtd.CostCodeTo = @costCodeTo AND vtd.LocCodeTo = @locCodeTo AND vtd.TrfNo IN @trfNos
                GROUP BY vtd.TrfNo, up.DivisionY, up.Department, up.Brand";
            var rows = await conn.QueryAsync<ChunkRow>(new CommandDefinition(
                sql, new { costCodeTo, locCodeTo, trfNos },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
        finally { throttle.Release(); }
    }

    private static void Accumulate(Dictionary<string, decimal> qtyByKey, string? key, decimal qty)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        qtyByKey[key] = qtyByKey.GetValueOrDefault(key) + qty;
    }

    private static string TopByQty(Dictionary<string, decimal> qtyByKey, int take) =>
        string.Join(",", qtyByKey.OrderByDescending(kv => kv.Value).Take(take).Select(kv => kv.Key));

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private record BaseRow(
        string Country, string GinNo, DateTime ReleasedDate, DateTime? EtaDate,
        string ShipNo, int TotalQty, int TransferCount,
        string ShopIssue, string ShopName, string TrfNo, string? Remarks, string? Whouse,
        string CostCodeTo, string LocCodeTo, string? DataName);

    private record ChunkRow(string TrfNo, string? Division, string? Department, string? Brand, decimal Qty);

    private const string BaseSql = @"
        SELECT DISTINCT
            ds.Country       AS Country,
            ep.GINNo         AS GinNo,
            ep.ReleasedDate  AS ReleasedDate,
            ep.ETADate       AS EtaDate,
            ep.Shipno        AS ShipNo,
            ep.TotalQty      AS TotalQty,
            ep.TransferCount AS TransferCount,
            gi.ShopIssue     AS ShopIssue,
            gi.ShopName      AS ShopName,
            gi.TrfNo         AS TrfNo,
            gi.Remarks       AS Remarks,
            gi.whouse        AS Whouse,
            ds.CostCodeTo    AS CostCodeTo,
            ds.LocCodeTo     AS LocCodeTo,
            ds.DataName      AS DataName
        FROM USA.dbo.ExportPass ep WITH (NOLOCK)
        JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
        JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
        WHERE ep.Trndate >= @since
          AND ep.ReleasedDate IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM bfldata..contreceiptexport cre WITH (NOLOCK) WHERE cre.ginno = ep.GINNo)
          AND (@country IS NULL OR ds.Country = @country)";
}
