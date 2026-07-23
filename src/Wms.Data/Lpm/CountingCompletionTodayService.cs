using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Counting Completion Report — "Today" mode. Unlike the regular Summary/
/// Allocation-wise/Detailed views (which read the nightly-batched
/// BFLDATA.dbo.BuildingCompletionSumm/Det tables), Today mode reads live,
/// same-day data straight from Online.dbo.PhotoCheckingResult (QtyIssue > 0)
/// for containers whose BFLDATA.dbo.BuildingCompletion.Trndate is today
/// (GST calendar day) — the nightly batch hasn't consolidated today's
/// counting into BuildingCompletionSumm/Det yet.
///
/// Each selected country is queried on its own connection: UAE via the
/// existing OnPremBackupDB connection, every other country via its own
/// {Country}_DB_ConnectionString (assumed to mirror the same Online/BFLDATA
/// schema) — countries with no connection string configured are skipped
/// silently, same fallback behaviour as GetProductionCheckingAsync in
/// ReportsService.
///
/// Brand (USA.dbo.UPCBarCodes.Vendor) and Box Category TypeName
/// (BFLDATA.dbo.PalletType.TypeName, keyed by PhotoCheckingResult.ResultType
/// — confirmed ResultType values are drawn from the same code space as
/// PalletType, e.g. 'KS' -> "EX2KSA") are both central-only master data
/// (UAE OnPremBackupDB), so they're looked up once for every distinct
/// UPC/ResultType across all countries' raw rows and joined in memory —
/// simpler and safer than bulk-copying every country's rows into a shared
/// temp table for a same-day data volume this small.
/// </summary>
public class CountingCompletionTodayService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private const string RawQuerySql = @"
        SELECT pcr.ContNo,
               pcr.UPC,
               pcr.Itemname AS ItemName,
               pcr.Division,
               pcr.ResultType,
               pcr.QtyIssue,
               pcr.LPMDt,
               pcr.ORAPONo
          FROM Online.dbo.PhotoCheckingResult pcr WITH (NOLOCK)
         WHERE pcr.QtyIssue > 0
           AND pcr.ContNo IN (
               SELECT ContNo FROM BFLDATA.dbo.BuildingCompletion WITH (NOLOCK)
                WHERE CAST(Trndate AS DATE) = @today)";

    private record RawRow(string ContNo, string UPC, string? ItemName, string? Division,
        string? ResultType, int QtyIssue, DateTime? LPMDt, string? ORAPONo);

    private record CountryRow(string Country, RawRow Row);

    private async Task<List<CountryRow>> FetchRawAsync(IReadOnlyList<string> countries, CancellationToken ct)
    {
        var today = DateTime.UtcNow.AddHours(4).Date;
        var all = new List<CountryRow>();

        foreach (var country in countries)
        {
            string? connStr;
            if (string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
            {
                connStr = resolver.GetOnPremBackupConnectionString();
            }
            else
            {
                try { connStr = resolver.GetCountryConnectionString(country); }
                catch { connStr = null; }
            }
            if (string.IsNullOrWhiteSpace(connStr)) continue; // not configured — skip silently

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);
            var rows = await conn.QueryAsync<RawRow>(new CommandDefinition(
                RawQuerySql, new { today }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            all.AddRange(rows.Select(r => new CountryRow(country, r)));
        }
        return all;
    }

    private async Task<(Dictionary<string, string?> brandByUpc, Dictionary<string, string?> typeNameByResultType)>
        EnrichAsync(IReadOnlyList<CountryRow> raw, CancellationToken ct)
    {
        var brandByUpc = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var typeNameByResultType = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var upcs = raw.Select(r => r.Row.UPC).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToArray();
        var resultTypes = raw.Select(r => r.Row.ResultType).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToArray();
        if (upcs.Length == 0 && resultTypes.Length == 0) return (brandByUpc, typeNameByResultType);

        await using var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        await c.OpenAsync(ct);

        if (upcs.Length > 0)
        {
            var brandRows = await c.QueryAsync<(string UPC, string? Vendor)>(new CommandDefinition(@"
                SELECT UPC, Vendor = MAX(Vendor)
                  FROM USA.dbo.UPCBarCodes WITH (NOLOCK)
                 WHERE UPC IN @upcs
                 GROUP BY UPC",
                new { upcs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in brandRows) brandByUpc[r.UPC] = r.Vendor;
        }

        if (resultTypes.Length > 0)
        {
            var typeRows = await c.QueryAsync<(string PalletType, string? TypeName)>(new CommandDefinition(@"
                SELECT PalletType, TypeName
                  FROM BFLDATA.dbo.PalletType WITH (NOLOCK)
                 WHERE PalletType IN @resultTypes",
                new { resultTypes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in typeRows) typeNameByResultType[r.PalletType] = r.TypeName;
        }

        return (brandByUpc, typeNameByResultType);
    }

    private static string? CommaJoin(IEnumerable<string?> values) =>
        string.Join(", ", values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

    public async Task<List<CountingCompletionTodaySummaryRow>> GetSummaryAsync(
        IReadOnlyList<string> countries, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(countries, ct);
        var (brandByUpc, typeNameByResultType) = await EnrichAsync(raw, ct);

        return raw
            .GroupBy(r => (r.Country, r.Row.ContNo))
            .Select(g => new CountingCompletionTodaySummaryRow(
                Country:     g.Key.Country,
                ContNo:      g.Key.ContNo,
                CountedQty:  g.Sum(x => x.Row.QtyIssue),
                LpmDates:    CommaJoin(g.Select(x => x.Row.LPMDt).Where(d => d.HasValue).Select(d => d!.Value.ToString("MMM-yyyy"))),
                Divisions:   CommaJoin(g.Select(x => x.Row.Division)),
                OraPoNos:    CommaJoin(g.Select(x => x.Row.ORAPONo)),
                Brands:      CommaJoin(g.Select(x => brandByUpc.GetValueOrDefault(x.Row.UPC))),
                PalletTypes: CommaJoin(g.Select(x => x.Row.ResultType)),
                TypeNames:   CommaJoin(g.Select(x => x.Row.ResultType).Where(rt => !string.IsNullOrWhiteSpace(rt)).Select(rt => typeNameByResultType.GetValueOrDefault(rt!)))))
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }

    public async Task<List<CountingCompletionTodayAllocationRow>> GetAllocationAsync(
        IReadOnlyList<string> countries, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(countries, ct);
        var (brandByUpc, typeNameByResultType) = await EnrichAsync(raw, ct);

        return raw
            .GroupBy(r => (r.Country, r.Row.ContNo, ResultType: r.Row.ResultType ?? "(none)"))
            .Select(g => new CountingCompletionTodayAllocationRow(
                Country:    g.Key.Country,
                ContNo:     g.Key.ContNo,
                ResultType: g.Key.ResultType,
                TypeName:   typeNameByResultType.GetValueOrDefault(g.Key.ResultType),
                BuildQty:   g.Sum(x => x.Row.QtyIssue),
                LpmDates:   CommaJoin(g.Select(x => x.Row.LPMDt).Where(d => d.HasValue).Select(d => d!.Value.ToString("MMM-yyyy"))),
                Divisions:  CommaJoin(g.Select(x => x.Row.Division)),
                OraPoNos:   CommaJoin(g.Select(x => x.Row.ORAPONo)),
                Brands:     CommaJoin(g.Select(x => brandByUpc.GetValueOrDefault(x.Row.UPC)))))
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }

    public async Task<List<CountingCompletionTodayDetailRow>> GetDetailAsync(
        IReadOnlyList<string> countries, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(countries, ct);
        var (brandByUpc, typeNameByResultType) = await EnrichAsync(raw, ct);

        return raw
            .GroupBy(r => (Country: r.Country, ContNo: r.Row.ContNo, UPC: r.Row.UPC))
            .Select(g =>
            {
                var palletType = g.Select(x => x.Row.ResultType).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
                return new CountingCompletionTodayDetailRow(
                    Country:    g.Key.Country,
                    ContNo:     g.Key.ContNo,
                    ItemCode:   g.Key.UPC,
                    ItemName:   g.Select(x => x.Row.ItemName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    Qty:        g.Sum(x => x.Row.QtyIssue),
                    LpmDt:      g.Select(x => x.Row.LPMDt).FirstOrDefault(d => d.HasValue),
                    Division:   g.Select(x => x.Row.Division).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    OraPoNo:    g.Select(x => x.Row.ORAPONo).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    Brand:      brandByUpc.GetValueOrDefault(g.Key.UPC),
                    PalletType: palletType,
                    TypeName:   palletType is null ? null : typeNameByResultType.GetValueOrDefault(palletType));
            })
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }
}
