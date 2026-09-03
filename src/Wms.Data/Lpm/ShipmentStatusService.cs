using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Shipment Status Report (Inbound) — merges what used to be two
/// separate reports (In-Transit Report + Container Receipt Report) into one:
/// every shipment released from UAE, whether still in transit or already
/// delivered, plus local/international containers received directly at the
/// destination country's own warehouse.
///
/// Everything lives on the single OnPremBackup (UAE master) server — country
/// DBs (P2EXPORT, BFLKSA, ...) are sibling catalogs reached via 3-part
/// naming, DataName resolved dynamically from bfldata.dbo.DataSettings.
///
/// Two shipment flows, unioned per country:
///
/// 1) "BFL" (GIN/export flow) — driven by USA.dbo.ExportPass (no exclusion
///    filter, unlike the old In-Transit Report, since we now want both
///    in-transit AND delivered rows together):
///      - GinDate: earliest bfldata..vGoodsIssueplt.EntryDate for that GIN.
///      - ReceiptDt: LEFT JOIN bfldata..contreceiptExport — NULL while in
///        transit. Status = "Delivered" once this is non-null, else
///        "InTransit".
///      - Division/Department/Brand: top-5-by-quantity-moved via per-shop
///        vTransferDetail + usa.USAPriority (same as the old In-Transit
///        Report).
///
/// 2) "LOCAL"/"International" — bfldata..ContReceipt filtered to the
///    country's own RecLocation(s) (DataSettings rows with Concept=
///    'warehouse'). These have no GIN/export flow at all, so GinNo/GinDate/
///    ReleasedOn/SLA-Shipping/ETA/BoxCount/ReceivedBoxes/BoxCountDiff are all
///    blank, and Status is always "Delivered" (ContReceipt IS the receipt
///    event — there's no earlier "released" record to compare against).
///    TotalQty and Division/Department/Brand come from usa..usaorgfile,
///    which carries its own GroupCode per line (same top-5-by-qty rule).
///
/// Type is derived from ShipNo: contains "LOC" → "LOCAL", contains "INT" →
/// "International", otherwise "JAFZA".
/// </summary>
public class ShipmentStatusService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;
    private const int ChunkSize = 2000; // larger chunks measured ~3x more throughput per TrfNo than 1000
    private const int MaxConcurrentChunkQueries = 8;
    private const int TopN = 5;

    // Floor for the in-transit branch only — the delivered branch is already
    // bounded by the selected Receipt Date range. Without this, "in transit"
    // pulls in years of stale/abandoned GINs that never got a contreceiptExport
    // row at all (observed: thousands from 2022-2025 vs. a handful genuinely
    // open this year), swamping the report with noise instead of open shipments.
    private static readonly DateTime InTransitFloor = new(DateTime.Today.Year, 1, 1);

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
              WHERE SIMCountry NOT IN ('', 'ECOM', 'Ex2Locations', 'UAE', 'OMAN')
              ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    public async Task<ShipmentStatusResult> GetShipmentStatusAsync(
        ShipmentStatusFilter f, CancellationToken ct = default)
    {
        var from = f.DateFrom.Date;
        var to   = f.DateTo.Date.AddDays(1).AddSeconds(-1);

        var countries = f.Countries is { Count: > 0 } ? f.Countries : await GetCountriesAsync(ct);
        var warnings = new List<string>();
        // Shared across every country in this call (not one per country): several
        // country catalogs alias the same physical DataName (e.g. P2EXPORT is used by
        // Bahrain, KSA, Kuwait, Malaysia, Qatar and Singapore alike — confirmed via
        // DataSettings). A per-country throttle let a "BFL Group" load fire up to
        // MaxConcurrentChunkQueries concurrent chunk queries PER country against that
        // one shared table simultaneously (6 countries x 8 = 48), which was fast in
        // isolation but piled up enough concurrent scans/hash-aggregates on the
        // on-prem server that whichever country's chunks got scheduled last (observed:
        // Kuwait) blew past the 120s command timeout. Capping total concurrency across
        // the whole request fixes that without slowing any single-country load.
        using var throttle = new SemaphoreSlim(MaxConcurrentChunkQueries);
        var tasks = countries.Select(async country =>
        {
            try
            {
                return await GetForCountryAsync(country, from, to, throttle, ct);
            }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{country}: {ex.Message}");
                return new List<ShipmentStatusRow>();
            }
        });

        var perCountry = await Task.WhenAll(tasks);
        var allRows = perCountry.SelectMany(r => r)
            .OrderBy(r => r.ReceiptDt ?? r.ReleasedOn ?? DateTime.MaxValue)
            .ToList();
        return new ShipmentStatusResult(allRows, warnings);
    }

    private async Task<List<ShipmentStatusRow>> GetForCountryAsync(
        string country, DateTime from, DateTime to, SemaphoreSlim throttle, CancellationToken ct)
    {
        var ginRows = await GetGinFlowRowsAsync(country, from, to, throttle, ct);
        var localRows = await GetLocalFlowRowsAsync(country, from, to, throttle, ct);
        return ginRows.Concat(localRows)
            .OrderBy(r => r.ReceiptDt ?? r.ReleasedOn ?? DateTime.MaxValue)
            .ToList();
    }

    // ===================== "BFL" (GIN/export) flow =====================

    private async Task<List<ShipmentStatusRow>> GetGinFlowRowsAsync(
        string country, DateTime from, DateTime to, SemaphoreSlim throttle, CancellationToken ct)
    {
        // Two narrow queries instead of one wide per-pallet query, run concurrently
        // (own connection each, since ADO.NET connections aren't safe for concurrent
        // commands): a small GROUP-BY-GinNo header query, and a 5-column GinNo/TrfNo/
        // shop-key mapping still needed at pallet granularity for the vTransferDetail
        // lookup below. Measured against live KSA data (full year, ~450 GINs / ~168k
        // pallets): the original single wide 17-column per-pallet query took ~17.4s;
        // splitting like this brings it to ~3-4s combined — most of the original cost
        // was shipping the same Remarks/ShopIssue/ShopName/Whouse text over the wire
        // hundreds of times per GIN, not the join itself.
        var headerTask  = RunHeaderQueryAsync(country, from, to, ct);
        var mappingTask = RunMappingQueryAsync(country, from, to, ct);
        await Task.WhenAll(headerTask, mappingTask);
        var headerRows  = headerTask.Result;
        var mappingRows = mappingTask.Result;

        if (headerRows.Count == 0) return [];

        // Kicked off now, awaited just before building rows, so it overlaps with the
        // (usually slower) vTransferDetail chunk queries below instead of adding to
        // the critical path.
        var receivedBoxesTask = GetReceivedBoxesByGinAsync(country, headerRows.Select(h => h.GinNo), ct);

        var shopGroups = mappingRows
            .GroupBy(r => (r.DataName, r.CostCodeTo, r.LocCodeTo))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.DataName))
            .ToList();

        var chunkTasks = new List<Task<List<GroupQtyRow>>>();
        foreach (var shop in shopGroups)
        {
            var trfNos = shop.Select(r => r.TrfNo).Distinct().ToList();
            foreach (var chunk in Chunk(trfNos, ChunkSize))
                chunkTasks.Add(RunTransferDetailChunkAsync(shop.Key.DataName!, shop.Key.CostCodeTo, shop.Key.LocCodeTo, chunk, throttle, ct));
        }

        var chunkResults = await Task.WhenAll(chunkTasks);
        var entriesByTrfNo = new Dictionary<string, List<GroupQtyRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rows in chunkResults)
            foreach (var r in rows)
            {
                if (!entriesByTrfNo.TryGetValue(r.ItemKey, out var list))
                    entriesByTrfNo[r.ItemKey] = list = [];
                list.Add(r);
            }

        var trfNosByGin = mappingRows
            .GroupBy(r => r.GinNo)
            .ToDictionary(g => g.Key, g => (IEnumerable<string>)g.Select(r => r.TrfNo).Distinct().ToList());

        var receivedBoxesByGin = await receivedBoxesTask;

        var result = new List<ShipmentStatusRow>();
        foreach (var h in headerRows)
        {
            var trfNos = trfNosByGin.TryGetValue(h.GinNo, out var list) ? list : [];
            var (division, department, brand) = RollUpTopN(trfNos, entriesByTrfNo);
            var receivedBoxes = receivedBoxesByGin.GetValueOrDefault(h.GinNo, 0);

            result.Add(new ShipmentStatusRow(
                Country:          country,
                ShipNo:           h.ShipNo,
                Type:             DetermineType(h.ShipNo),
                Status:           h.ReceiptDt.HasValue ? "Delivered" : "InTransit",
                GinNo:            h.GinNo,
                GinDate:          h.EntryDate,
                ReleasedOn:       h.ReleasedOn,
                SlaShippingDays:  DayDiff(h.EntryDate, h.ReleasedOn),
                Eta:              h.Eta,
                TotalQty:         h.TotalQty,
                BoxCount:         h.TransferCount,
                ReceiptDt:        h.ReceiptDt,
                SlaReceiptDays:   DayDiff(h.ReleasedOn, h.ReceiptDt),
                ReceivedBoxes:    receivedBoxes,
                BoxCountDiff:     h.TransferCount - receivedBoxes,
                Remarks:          h.Remarks ?? "",
                Division:         division,
                Department:       department,
                Brand:            brand));
        }

        return result;
    }

    private async Task<List<GinHeaderRow>> RunHeaderQueryAsync(
        string country, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var conn = OpenOnPremBackup();
        var rows = await conn.QueryAsync<GinHeaderRow>(new CommandDefinition(
            GinHeaderSql, new { from, to, country, inTransitFloor = InTransitFloor },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private async Task<List<GinMappingRow>> RunMappingQueryAsync(
        string country, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var conn = OpenOnPremBackup();
        var rows = await conn.QueryAsync<GinMappingRow>(new CommandDefinition(
            GinMappingSql, new { from, to, country, inTransitFloor = InTransitFloor },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private record VerifyGinCountRow(string GinNo, int ReceivedBoxes);

    // Boxes verified as RECEIVED at the destination warehouse always live in the
    // country's own primary catalog (e.g. BFLKSA for KSA) — resolved the same way
    // WhBoxItemsSource does it (bfldata.dbo.DataSettings.SIMCountry), NOT the
    // per-shop DataName used for transfer routing above. Confirmed live: a GIN
    // routed through KSA's special export shop (EX2KSA/P2EXPORT) still verifies
    // its boxes in BFLKSA..VerifyGin, not P2EXPORT..VerifyGin. The previous version
    // of this query read bfldata..VerifyGin directly, which is a different/stale
    // table that had rows for GINs still genuinely InTransit (no contreceiptExport
    // row yet) — giving a nonsensical "received boxes on an InTransit shipment".
    private async Task<Dictionary<string, int>> GetReceivedBoxesByGinAsync(
        string country, IEnumerable<string> ginNos, CancellationToken ct)
    {
        var ginNoList = ginNos.Distinct().ToList();
        if (ginNoList.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        await using var conn = OpenOnPremBackup();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(conn, country, ct);
        if (dataName is null) return new(StringComparer.OrdinalIgnoreCase);

        var sql = $@"
            SELECT CAST(GinNo AS VARCHAR(20)) AS GinNo, COUNT(TrfNo) AS ReceivedBoxes
            FROM [{dataName}].dbo.VerifyGin WITH (NOLOCK)
            WHERE Verified = 'Y' AND GinNo IN @ginNos
            GROUP BY GinNo";
        var rows = await conn.QueryAsync<VerifyGinCountRow>(new CommandDefinition(
            sql, new { ginNos = ginNoList }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.ToDictionary(r => r.GinNo, r => r.ReceivedBoxes, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<GroupQtyRow>> RunTransferDetailChunkAsync(
        string dataName, string costCodeTo, string locCodeTo, List<string> trfNos,
        SemaphoreSlim throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            await using var conn = OpenOnPremBackup();
            // Literal IN list, not Dapper's `IN @trfNos` (which binds one SqlParameter per
            // item) and not STRING_SPLIT: live profiling against Kuwait's shop groups (a
            // 4-week load: 9,978 TrfNos against P2EXPORT + 1,075 against BFLKUWAIT, run
            // concurrently) showed Dapper's per-item parameter binding balloons a 2000-row
            // chunk from ~1.5s to ~8s under concurrent load (ad-hoc plan compilation cost
            // scales with parameter count), and STRING_SPLIT — while fine for P2EXPORT —
            // made BFLKUWAIT's vTransferDetail time out even in complete isolation (bad
            // cardinality estimate/join plan for that catalog specifically, even with a
            // forced hash join). The literal list avoided both problems in every test.
            var sql = $@"
                SELECT vtd.TrfNo AS ItemKey, up.DivisionY AS Division, up.Department, up.Brand, SUM(vtd.Quantity) AS Qty
                FROM [{dataName}].dbo.vTransferDetail vtd WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = vtd.groupcode
                WHERE vtd.CostCodeTo = @costCodeTo AND vtd.LocCodeTo = @locCodeTo AND vtd.TrfNo IN ({BuildInClause(trfNos)})
                GROUP BY vtd.TrfNo, up.DivisionY, up.Department, up.Brand";
            var rows = await conn.QueryAsync<GroupQtyRow>(new CommandDefinition(
                sql, new { costCodeTo, locCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
        finally { throttle.Release(); }
    }

    // TrfNo/ContNo values are sourced from our own prior query result (never user input),
    // but still escaped defensively before being embedded as SQL literals.
    private static string BuildInClause(IEnumerable<string> values) =>
        string.Join(",", values.Select(v => "'" + v.Replace("'", "''") + "'"));

    private record GinHeaderRow(
        string GinNo, DateTime ReleasedOn, DateTime? Eta, string ShipNo, int TotalQty, int TransferCount,
        DateTime? EntryDate, string? Remarks, DateTime? ReceiptDt);

    private record GinMappingRow(string GinNo, string TrfNo, string CostCodeTo, string LocCodeTo, string? DataName);

    // Same driver/filter as GinMappingSql below, aggregated to one row per GIN.
    // ReceivedBoxes (destination box verification) is NOT sourced here — see
    // GetReceivedBoxesByGinAsync below for why.
    private const string GinHeaderSql = @"
        SELECT
            ep.GINNo         AS GinNo,
            ep.Trndate       AS ReleasedOn,
            ep.ETADate       AS Eta,
            ep.Shipno        AS ShipNo,
            ep.TotalQty      AS TotalQty,
            ep.TransferCount AS TransferCount,
            MIN(gi.EntryDate) AS EntryDate,
            MAX(gi.Remarks)   AS Remarks,
            cre.ReceiptDt    AS ReceiptDt
        FROM USA.dbo.ExportPass ep WITH (NOLOCK)
        JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
        JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
        LEFT JOIN bfldata..contreceiptExport cre WITH (NOLOCK) ON cre.GINNO = ep.GINNo
        WHERE (@country IS NULL OR ds.Country = @country)
          AND (
                (cre.ReceiptDt IS NOT NULL AND cre.ReceiptDt >= @from AND cre.ReceiptDt <= @to)
             OR (cre.ReceiptDt IS NULL AND ep.Trndate <= @to AND ep.Trndate >= @inTransitFloor)
              )
        GROUP BY ep.GINNo, ep.Trndate, ep.ETADate, ep.Shipno, ep.TotalQty, ep.TransferCount, cre.ReceiptDt";

    // Pallet-level GinNo/TrfNo/shop-key mapping — still needed at this granularity for
    // the vTransferDetail lookup, but only these 5 narrow columns (not Remarks/
    // ShopIssue/ShopName/Whouse repeated per pallet, which is what made the old
    // single combined query slow to transfer).
    private const string GinMappingSql = @"
        SELECT
            ep.GINNo      AS GinNo,
            gi.TrfNo      AS TrfNo,
            ds.CostCodeTo AS CostCodeTo,
            ds.LocCodeTo  AS LocCodeTo,
            ds.DataName   AS DataName
        FROM USA.dbo.ExportPass ep WITH (NOLOCK)
        JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
        JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
        LEFT JOIN bfldata..contreceiptExport cre WITH (NOLOCK) ON cre.GINNO = ep.GINNo
        WHERE (@country IS NULL OR ds.Country = @country)
          AND (
                (cre.ReceiptDt IS NOT NULL AND cre.ReceiptDt >= @from AND cre.ReceiptDt <= @to)
             OR (cre.ReceiptDt IS NULL AND ep.Trndate <= @to AND ep.Trndate >= @inTransitFloor)
              )";

    // ===================== "LOCAL"/"International" flow =====================

    private async Task<List<ShipmentStatusRow>> GetLocalFlowRowsAsync(
        string country, DateTime from, DateTime to, SemaphoreSlim throttle, CancellationToken ct)
    {
        await using var conn = OpenOnPremBackup();
        var localRows = (await conn.QueryAsync<LocalBaseRow>(new CommandDefinition(
            LocalBaseSql, new { country, from, to },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (localRows.Count == 0) return [];

        var contNos = localRows.Select(r => r.ShipNo).Distinct().ToList();
        var chunkTasks = Chunk(contNos, ChunkSize)
            .Select(chunk => RunUsaOrgFileChunkAsync(chunk, throttle, ct));
        var chunkResults = await Task.WhenAll(chunkTasks);

        var entriesByContNo = new Dictionary<string, List<GroupQtyRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rows in chunkResults)
            foreach (var r in rows)
            {
                if (!entriesByContNo.TryGetValue(r.ItemKey, out var list))
                    entriesByContNo[r.ItemKey] = list = [];
                list.Add(r);
            }

        var result = new List<ShipmentStatusRow>();
        foreach (var r in localRows)
        {
            var (division, department, brand) = RollUpTopN([r.ShipNo], entriesByContNo);
            var totalQty = entriesByContNo.TryGetValue(r.ShipNo, out var entries)
                ? entries.Sum(e => e.Qty) : 0;

            result.Add(new ShipmentStatusRow(
                Country:         country,
                ShipNo:          r.ShipNo,
                Type:            DetermineType(r.ShipNo),
                Status:          "Delivered",
                GinNo:           "",
                GinDate:         null,
                ReleasedOn:      null,
                SlaShippingDays: null,
                Eta:             null,
                TotalQty:        (int)totalQty,
                BoxCount:        null,
                ReceiptDt:       r.ReceiptDt,
                SlaReceiptDays:  null,
                ReceivedBoxes:   null,
                BoxCountDiff:    null,
                Remarks:         "",
                Division:        division,
                Department:      department,
                Brand:           brand));
        }

        return result;
    }

    private async Task<List<GroupQtyRow>> RunUsaOrgFileChunkAsync(
        List<string> contNos, SemaphoreSlim throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            await using var conn = OpenOnPremBackup();
            var sql = $@"
                SELECT uo.ContNo AS ItemKey, up.DivisionY AS Division, up.Department, up.Brand, CAST(SUM(uo.orgqty) AS DECIMAL(18,2)) AS Qty
                FROM usa..usaorgfile uo WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = uo.GroupCode
                WHERE uo.ContNo IN ({BuildInClause(contNos)})
                GROUP BY uo.ContNo, up.DivisionY, up.Department, up.Brand";
            var rows = await conn.QueryAsync<GroupQtyRow>(new CommandDefinition(
                sql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
        finally { throttle.Release(); }
    }

    private record LocalBaseRow(string ShipNo, DateTime ReceiptDt);

    private const string LocalBaseSql = @"
        SELECT
            cr.ContNo    AS ShipNo,
            cr.ReceiptDt AS ReceiptDt
        FROM bfldata..ContReceipt cr WITH (NOLOCK)
        WHERE cr.RecLocation IN (
            SELECT shopname FROM bfldata..datasettings WITH (NOLOCK)
            WHERE concept = 'warehouse' AND country = @country AND shopname LIKE 'BFL%'
        )
        AND cr.ReceiptDt >= @from AND cr.ReceiptDt <= @to";

    // ===================== shared helpers =====================

    private record GroupQtyRow(string ItemKey, string? Division, string? Department, string? Brand, decimal Qty);

    private static (string Division, string Department, string Brand) RollUpTopN(
        IEnumerable<string> keys, Dictionary<string, List<GroupQtyRow>> entriesByKey)
    {
        var qtyByDivision   = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var qtyByDepartment = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var qtyByBrand      = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (!entriesByKey.TryGetValue(key, out var entries)) continue;
            foreach (var e in entries)
            {
                Accumulate(qtyByDivision,   e.Division,   e.Qty);
                Accumulate(qtyByDepartment, e.Department, e.Qty);
                Accumulate(qtyByBrand,      e.Brand,      e.Qty);
            }
        }

        return (TopByQty(qtyByDivision), TopByQty(qtyByDepartment), TopByQty(qtyByBrand));
    }

    private static void Accumulate(Dictionary<string, decimal> qtyByKey, string? key, decimal qty)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        qtyByKey[key] = qtyByKey.GetValueOrDefault(key) + qty;
    }

    private static string TopByQty(Dictionary<string, decimal> qtyByKey) =>
        string.Join(",", qtyByKey.OrderByDescending(kv => kv.Value).Take(TopN).Select(kv => kv.Key));

    // "Source Location" in the UI — JAFZA is the physical UAE origin warehouse for
    // the GIN/export flow; LOCAL/International containers aren't sourced from there.
    private static string DetermineType(string shipNo) =>
        shipNo.Contains("LOC", StringComparison.OrdinalIgnoreCase) ? "LOCAL"
      : shipNo.Contains("INT", StringComparison.OrdinalIgnoreCase) ? "International"
      : "JAFZA";

    private static int? DayDiff(DateTime? from, DateTime? to) =>
        from.HasValue && to.HasValue ? (to.Value.Date - from.Value.Date).Days : null;

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    // ===================== Division x Month drill-down =====================
    // Popup shown when an Intransit number or a GIN No. is clicked — same vTransferDetail
    // source as the Division/Department/Brand rollup above, but grouped by
    // (DivisionY, LpmDt month) instead of top-5-collapsed. Intransit-only (not
    // Delivered) because Delivered can also come from the separate ContReceipt/
    // usaorgfile flow for LOCAL/International, which has no LpmDt to group by.

    private record TrfShopKey(string TrfNo, string CostCodeTo, string LocCodeTo, string? DataName);
    private record TrfShopKeyWithShip(string TrfNo, string CostCodeTo, string LocCodeTo, string? DataName, string ShipNo);
    private record DivisionMonthChunkRow(string? Division, int? Year, int? Month, decimal Qty);

    private const string NoDateKey = "(no date)";

    public async Task<DivisionMonthSummaryResult> GetDivisionMonthSummaryByGinAsync(
        string ginNo, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var entries = (await conn.QueryAsync<TrfShopKey>(new CommandDefinition(@"
            SELECT gi.TrfNo AS TrfNo, ds.CostCodeTo AS CostCodeTo, ds.LocCodeTo AS LocCodeTo, ds.DataName AS DataName
            FROM USA.dbo.ExportPass ep WITH (NOLOCK)
            JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
            JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
            WHERE ep.GINNo = @ginNo",
            new { ginNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        return await BuildDivisionMonthSummaryAsync(entries, ct);
    }

    // type: "JAFZA" | "LOCAL" | "International" | null (Total — every type combined).
    public async Task<DivisionMonthSummaryResult> GetIntransitDivisionMonthSummaryAsync(
        string? country, string? type, DateTime to, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var raw = (await conn.QueryAsync<TrfShopKeyWithShip>(new CommandDefinition(@"
            SELECT gi.TrfNo AS TrfNo, ds.CostCodeTo AS CostCodeTo, ds.LocCodeTo AS LocCodeTo, ds.DataName AS DataName, ep.Shipno AS ShipNo
            FROM USA.dbo.ExportPass ep WITH (NOLOCK)
            JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
            JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
            LEFT JOIN bfldata..contreceiptExport cre WITH (NOLOCK) ON cre.GINNO = ep.GINNo
            WHERE (@country IS NULL OR ds.Country = @country)
              AND cre.ReceiptDt IS NULL
              AND ep.Trndate <= @to AND ep.Trndate >= @inTransitFloor",
            new { country, to, inTransitFloor = InTransitFloor },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var filtered = string.IsNullOrEmpty(type)
            ? raw
            : raw.Where(r => DetermineType(r.ShipNo) == type).ToList();

        var entries = filtered
            .Select(r => new TrfShopKey(r.TrfNo, r.CostCodeTo, r.LocCodeTo, r.DataName))
            .ToList();

        return await BuildDivisionMonthSummaryAsync(entries, ct);
    }

    private async Task<DivisionMonthSummaryResult> BuildDivisionMonthSummaryAsync(
        List<TrfShopKey> entries, CancellationToken ct)
    {
        var shopGroups = entries
            .GroupBy(e => (e.DataName, e.CostCodeTo, e.LocCodeTo))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.DataName))
            .ToList();

        using var throttle = new SemaphoreSlim(MaxConcurrentChunkQueries);
        var chunkTasks = new List<Task<List<DivisionMonthChunkRow>>>();
        foreach (var shop in shopGroups)
        {
            var trfNos = shop.Select(e => e.TrfNo).Distinct().ToList();
            foreach (var chunk in Chunk(trfNos, ChunkSize))
                chunkTasks.Add(RunDivisionMonthChunkAsync(shop.Key.DataName!, shop.Key.CostCodeTo, shop.Key.LocCodeTo, chunk, throttle, ct));
        }
        var chunkResults = (await Task.WhenAll(chunkTasks)).SelectMany(r => r).ToList();

        return BuildPivot(chunkResults);
    }

    private async Task<List<DivisionMonthChunkRow>> RunDivisionMonthChunkAsync(
        string dataName, string costCodeTo, string locCodeTo, List<string> trfNos,
        SemaphoreSlim throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            await using var conn = OpenOnPremBackup();
            var sql = $@"
                SELECT up.DivisionY AS Division, YEAR(vtd.LpmDt) AS Year, MONTH(vtd.LpmDt) AS Month, SUM(vtd.Quantity) AS Qty
                FROM [{dataName}].dbo.vTransferDetail vtd WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = vtd.groupcode
                WHERE vtd.CostCodeTo = @costCodeTo AND vtd.LocCodeTo = @locCodeTo AND vtd.TrfNo IN ({BuildInClause(trfNos)})
                GROUP BY up.DivisionY, YEAR(vtd.LpmDt), MONTH(vtd.LpmDt)";
            var rows = await conn.QueryAsync<DivisionMonthChunkRow>(new CommandDefinition(
                sql, new { costCodeTo, locCodeTo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
        finally { throttle.Release(); }
    }

    private static DivisionMonthSummaryResult BuildPivot(List<DivisionMonthChunkRow> rows)
    {
        static string KeyOf(int? y, int? m) => y.HasValue && m.HasValue ? $"{y:D4}-{m:D2}" : NoDateKey;
        static string LabelOf(string key) =>
            key == NoDateKey ? NoDateKey : new DateTime(int.Parse(key[..4]), int.Parse(key[5..]), 1).ToString("MMM-yy");

        var cellQty = new Dictionary<(string Division, string MonthKey), decimal>();
        foreach (var r in rows)
        {
            var division = string.IsNullOrWhiteSpace(r.Division) ? "(blank)" : r.Division!;
            var key = (division, KeyOf(r.Year, r.Month));
            cellQty[key] = cellQty.GetValueOrDefault(key) + r.Qty;
        }

        var datedKeys = cellQty.Keys.Select(k => k.MonthKey).Where(k => k != NoDateKey).Distinct()
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var hasNoDate = cellQty.Keys.Any(k => k.MonthKey == NoDateKey);
        var monthKeys = hasNoDate ? new List<string> { NoDateKey }.Concat(datedKeys).ToList() : datedKeys;
        var monthLabels = monthKeys.Select(LabelOf).ToList();

        var divisions = cellQty.Keys.Select(k => k.Division).Distinct()
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        var divisionRows = divisions.Select(division =>
        {
            var monthQty = monthKeys.Select(mk => cellQty.GetValueOrDefault((division, mk))).ToList();
            return new DivisionMonthRow(division, monthQty, monthQty.Sum());
        }).ToList();

        var monthTotals = Enumerable.Range(0, monthKeys.Count)
            .Select(i => divisionRows.Sum(r => r.MonthQty[i]))
            .ToList();

        return new DivisionMonthSummaryResult(monthLabels, divisionRows, monthTotals, monthTotals.Sum());
    }
}
