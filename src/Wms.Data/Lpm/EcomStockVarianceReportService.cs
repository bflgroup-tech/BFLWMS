using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record EcomStockVarianceRow(
    string Country, string Itemcode, int IncreffSOH, int MFCS_SOH, int Variance, DateTime CreateTS,
    string? Division, string? Department, string? Class, string? Subclass, string? Family);

public record EcomStockVarianceTotals(int RowCount, long IncreffSOH, long MFCS_SOH, long Variance);

/// <summary>
/// Backing service for the ECOM Stock Variance Report — reads
/// dbo.LPM_ECOM_SOH_COMPARISON directly. Division/Department/Class/Subclass/
/// Family are denormalized into that table at write time by
/// IncreffMfcsSohCompareService's Refresh Now (from DATAREPORTING.dbo.vUPC_SUBCLASS),
/// so this report no longer joins the 20M-row view itself at read time.
///
/// Filterable by Country, Division, and "Variance only" (Variance &lt;&gt; 0) — all
/// three are applied server-side (FilterWhereSql), not client-side, because the
/// on-screen grid is real-paged (GetReportPageAsync): filtering after paging
/// would mean a page of 500 raw rows could shrink to a handful once "Variance
/// only" is applied, breaking the page-size guarantee. GetTotalsAsync sums the
/// WHOLE filtered set (not just the current page) for the totals row.
///
/// GetReportAsync (unpaged) still exists for Excel export — a one-off, user-
/// initiated action where a large row count is a memory/time cost, not the
/// "hang the browser rendering it into a live DOM" problem paging exists for.
/// </summary>
public class EcomStockVarianceReportService(IOnPremConnectionResolver resolver)
{
    public const int PageSize = 500;

    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    /// <summary>Countries actually present in LPM_ECOM_SOH_COMPARISON (currently UAE/KSA).</summary>
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Country FROM dbo.LPM_ECOM_SOH_COMPARISON ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>Division list for the filter — every distinct Division actually
    /// present in LPM_ECOM_SOH_COMPARISON (denormalized at write time).</summary>
    public async Task<List<string>> GetDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Division FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE Division IS NOT NULL AND Division <> ''
             ORDER BY Division",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // countries/divisions: null means unrestricted; an empty-but-non-null list means
    // "match nothing" (a deny-by-default caller with zero country grants) — same
    // convention as ReportsService.GetPoCountingAsync.
    private const string FilterWhereSql = @"
             WHERE (@noCountryFilter = 1 OR Country IN @countries)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@varianceOnly = 0 OR Variance <> 0)";

    private static object BuildFilterParams(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, bool varianceOnly) => new
    {
        countries = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noCountryFilter = countries is null ? 1 : 0,
        divisions = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDivisionFilter = divisions is null ? 1 : 0,
        varianceOnly = varianceOnly ? 1 : 0,
    };

    /// <summary>Row count + sums over the WHOLE filtered set, for the totals row and
    /// the pager's "N rows / M pages" display.</summary>
    public async Task<EcomStockVarianceTotals> GetTotalsAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, bool varianceOnly, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var totals = await c.QuerySingleAsync<EcomStockVarianceTotals>(new CommandDefinition($@"
            SELECT COUNT(*) AS [RowCount],
                   ISNULL(SUM(CAST(IncreffSOH AS BIGINT)), 0) AS IncreffSOH,
                   ISNULL(SUM(CAST(MFCS_SOH AS BIGINT)), 0)   AS MFCS_SOH,
                   ISNULL(SUM(CAST(Variance AS BIGINT)), 0)   AS Variance
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql};",
            BuildFilterParams(countries, divisions, varianceOnly),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return totals;
    }

    /// <summary>One page (PageSize rows) of the filtered set, 1-indexed.</summary>
    public async Task<List<EcomStockVarianceRow>> GetReportPageAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, bool varianceOnly, int pageNumber,
        CancellationToken ct = default)
    {
        var offset = Math.Max(0, pageNumber - 1) * PageSize;
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH, Variance, CreateTS,
                   Division, Department, Class, Subclass, Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql}
             ORDER BY Country, Itemcode
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;",
            new
            {
                countries = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
                noCountryFilter = countries is null ? 1 : 0,
                divisions = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
                noDivisionFilter = divisions is null ? 1 : 0,
                varianceOnly = varianceOnly ? 1 : 0,
                offset,
                pageSize = PageSize,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Unpaged — every row in the filtered set. Used only by Excel export,
    /// a one-off user-initiated action; not used for the on-screen grid.</summary>
    public async Task<List<EcomStockVarianceRow>> GetReportAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, bool varianceOnly, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH, Variance, CreateTS,
                   Division, Department, Class, Subclass, Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql}
             ORDER BY Country, Itemcode;",
            BuildFilterParams(countries, divisions, varianceOnly),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
