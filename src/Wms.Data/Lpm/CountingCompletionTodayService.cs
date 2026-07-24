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
///
/// Connection reuse: opening a fresh connection to the on-prem server has
/// real latency from Azure App Service (TCP+TLS+login), so UAE's raw fetch
/// and the enrichment queries all share ONE OnPremBackupDB connection
/// (opened once per report call) instead of one connection per query, and
/// the two enrichment lookups run as a single QueryMultipleAsync batch
/// instead of two round trips.
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

    private async Task<List<CountryRow>> FetchRawAsync(
        IReadOnlyList<string> countries, SqlConnection onPremBackup, CancellationToken ct)
    {
        var today = DateTime.UtcNow.AddHours(4).Date;
        var all = new List<CountryRow>();

        foreach (var country in countries)
        {
            // MALAYSIA's connection string is configured but its server doesn't have
            // the Online database this report needs — excluded for now until that's
            // sorted out, rather than surfacing a raw SQL error to the user.
            if (string.Equals(country, "MALAYSIA", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
            {
                // Reuse the shared connection — UAE's data and the enrichment lookups
                // both live on OnPremBackupDB, so there's no need for a second connection.
                var rows = await onPremBackup.QueryAsync<RawRow>(new CommandDefinition(
                    RawQuerySql, new { today }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                all.AddRange(rows.Select(r => new CountryRow(country, r)));
                continue;
            }

            string? connStr;
            try { connStr = resolver.GetCountryConnectionString(country); }
            catch { connStr = null; }
            if (string.IsNullOrWhiteSpace(connStr)) continue; // not configured — skip silently

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(ct);
                var rows = await conn.QueryAsync<RawRow>(new CommandDefinition(
                    RawQuerySql, new { today }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                all.AddRange(rows.Select(r => new CountryRow(country, r)));
            }
            catch
            {
                // That country's server is reachable but doesn't have what this query
                // needs (e.g. no Online database) — skip it rather than failing the
                // whole report for every other selected country.
            }
        }
        return all;
    }

    private async Task<(Dictionary<string, string?> brandByUpc, Dictionary<string, string?> typeNameByResultType)>
        EnrichAsync(IReadOnlyList<CountryRow> raw, SqlConnection onPremBackup, CancellationToken ct)
    {
        var brandByUpc = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var typeNameByResultType = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var upcs = raw.Select(r => r.Row.UPC).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToArray();
        var resultTypes = raw.Select(r => r.Row.ResultType).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToArray();
        if (upcs.Length == 0 && resultTypes.Length == 0) return (brandByUpc, typeNameByResultType);

        // Single round trip for both lookups instead of two sequential QueryAsync calls.
        await using var multi = await onPremBackup.QueryMultipleAsync(new CommandDefinition(@"
            SELECT UPC, Vendor = MAX(Vendor)
              FROM USA.dbo.UPCBarCodes WITH (NOLOCK)
             WHERE @hasUpcs = 1 AND UPC IN @upcs
             GROUP BY UPC;

            SELECT PalletType, TypeName
              FROM BFLDATA.dbo.PalletType WITH (NOLOCK)
             WHERE @hasTypes = 1 AND PalletType IN @resultTypes;",
            new
            {
                hasUpcs = upcs.Length > 0 ? 1 : 0,
                upcs = upcs.Length > 0 ? upcs : new[] { "" },
                hasTypes = resultTypes.Length > 0 ? 1 : 0,
                resultTypes = resultTypes.Length > 0 ? resultTypes : new[] { "" }
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var brandRows = await multi.ReadAsync<(string UPC, string? Vendor)>();
        foreach (var r in brandRows) brandByUpc[r.UPC] = r.Vendor;

        var typeRows = await multi.ReadAsync<(string PalletType, string? TypeName)>();
        foreach (var r in typeRows) typeNameByResultType[r.PalletType] = r.TypeName;

        return (brandByUpc, typeNameByResultType);
    }

    private async Task<(List<CountryRow> raw, Dictionary<string, string?> brandByUpc, Dictionary<string, string?> typeNameByResultType)>
        FetchAndEnrichAsync(IReadOnlyList<string> countries, CancellationToken ct)
    {
        await using var onPremBackup = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        await onPremBackup.OpenAsync(ct);

        var raw = await FetchRawAsync(countries, onPremBackup, ct);
        var (brandByUpc, typeNameByResultType) = await EnrichAsync(raw, onPremBackup, ct);
        return (raw, brandByUpc, typeNameByResultType);
    }

    private static string? CommaJoin(IEnumerable<string?> values) =>
        string.Join(", ", values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

    public async Task<List<CountingCompletionTodaySummaryRow>> GetSummaryAsync(
        IReadOnlyList<string> countries, CancellationToken ct = default)
    {
        var (raw, brandByUpc, typeNameByResultType) = await FetchAndEnrichAsync(countries, ct);

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
        var (raw, brandByUpc, typeNameByResultType) = await FetchAndEnrichAsync(countries, ct);

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
        var (raw, brandByUpc, typeNameByResultType) = await FetchAndEnrichAsync(countries, ct);

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
