using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record EcomStockVarianceRow(
    string Country, string Itemcode, int IncreffSOH, int MFCS_SOH,
    int GateKeeperRejectedSummer, int GateKeeperRejectedWinter, int Variance,
    int InTransitUAE, int InTransitKSA, DateTime CreateTS,
    string? Division, string? Department, string? Class, string? Subclass, string? Family, string? Brand);

public record EcomStockVarianceTotals(
    int RowCount, long IncreffSOH, long MFCS_SOH,
    long GateKeeperRejectedSummer, long GateKeeperRejectedWinter, long Variance,
    long InTransitUAE, long InTransitKSA);

public record EcomStockVarianceDivisionSummaryRow(
    string Country, string? Division, int ItemCount, long IncreffSOH, long MFCS_SOH,
    long GateKeeperRejectedSummer, long GateKeeperRejectedWinter, long Variance,
    long InTransitUAE, long InTransitKSA);

/// <summary>Whole-table KPI snapshot for the Dashboard tab — deliberately unfiltered
/// (no Country/Division/Department/Brand), a fixed top-level view independent of
/// whatever the other two tabs' filters are set to. NetVariancePercent and
/// ExactMatchSkuPercent are computed here (not in SQL) since both are simple ratios
/// of values already fetched.</summary>
public record EcomStockVarianceDashboardSummary(
    long MfcsSoh, long IncreffSoh, long NetVariance, long GrossGap,
    int VarianceSkuCount, int PositiveStockSkuCount, int ExactMatchPositiveStockSkuCount, int RowsReviewed)
{
    public double NetVariancePercent => IncreffSoh == 0 ? 0 : NetVariance * 100.0 / IncreffSoh;
    public double ExactMatchSkuPercent =>
        PositiveStockSkuCount == 0 ? 0 : ExactMatchPositiveStockSkuCount * 100.0 / PositiveStockSkuCount;
}

/// <summary>One row of the Dashboard's "Net Variance by Reconciliation Bucket" table.
/// IncreffSoh here is IncreffSOH+GateKeeperRejectedSummer+GateKeeperRejectedWinter (the
/// same adjustment already baked into the Variance column) — NOT the raw IncreffSOH
/// shown elsewhere, so NetVariance always equals MfcsSoh - IncreffSoh exactly, and
/// stays the same "Variance" everywhere in this report. GrossGapSharePercent is
/// computed after fetching all buckets (needs the grand total across buckets).</summary>
public record EcomStockVarianceReconciliationBucket(
    string Bucket, int SkuCount, long MfcsSoh, long IncreffSoh, long NetVariance, double GrossGapSharePercent);

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

    /// <summary>When IncreffMfcsSohCompareService last rebuilt the table — every row
    /// shares the same CreateTS (one TRUNCATE + INSERT per run), so MAX is exact,
    /// not an approximation. Independent of Country/Division/Variance filters, so
    /// it still shows a value even when the current filter matches zero rows.</summary>
    public async Task<DateTime?> GetLastRefreshedAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(CreateTS) FROM dbo.LPM_ECOM_SOH_COMPARISON",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
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

    // Junk/placeholder divisions that never represent a real classification —
    // rows are KEPT (they still carry real SOH/Variance data), but Division/
    // Department/Class/Subclass/Family are blanked to NULL for them (see
    // ClassificationSelectSql) rather than showing the placeholder text. Same
    // "DATA MIGRATION -D" placeholder ReportsService already excludes from the
    // PO Counting Report's Division filter.
    private static readonly string[] ExcludedDivisions = ["DATA MIGRATION -D", "DATA MIGRATION", "BFL Services"];

    /// <summary>Division list for the filter — every distinct Division actually
    /// present in LPM_ECOM_SOH_COMPARISON (denormalized at write time), minus the
    /// junk placeholders that are blanked out in the report itself.</summary>
    public async Task<List<string>> GetDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Division FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE Division IS NOT NULL AND Division <> '' AND Division NOT IN @excluded
             ORDER BY Division",
            new { excluded = ExcludedDivisions },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>Department list for the filter — every distinct Department actually
    /// present in LPM_ECOM_SOH_COMPARISON, minus rows whose Division is one of the
    /// junk placeholders (same exclusion as GetDivisionsAsync — a junk-division row's
    /// Department is blanked out in the report itself, so it shouldn't appear here
    /// either). ~120 distinct values — small enough for a multi-select, like Division.</summary>
    public async Task<List<string>> GetDepartmentsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Department FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE Department IS NOT NULL AND Department <> '' AND Division NOT IN @excluded
             ORDER BY Department",
            new { excluded = ExcludedDivisions },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // Blanks Division/Department/Class/Subclass/Family to NULL when Division is
    // one of the junk placeholders — the row itself is still selected, only the
    // classification columns are hidden.
    private const string ClassificationSelectSql = @"
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Division   END AS Division,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Department END AS Department,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Class      END AS Class,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Subclass   END AS Subclass,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Family     END AS Family";

    // countries/divisions/departments: null means unrestricted; an empty-but-non-null
    // list means "match nothing" (a deny-by-default caller with zero country grants) —
    // same convention as ReportsService.GetPoCountingAsync. brandSearch is a plain
    // substring match (Brand has ~4,100 distinct values — too many for a dropdown,
    // unlike Division/Department). itemcodeSearch is an exact match, same convention
    // as EmpCode-style filters elsewhere in this app.
    private const string FilterWhereSql = @"
             WHERE (@noCountryFilter = 1 OR Country IN @countries)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@noDepartmentFilter = 1 OR Department IN @departments)
               AND (@brandFilter IS NULL OR Brand LIKE '%' + @brandFilter + '%')
               AND (@itemcodeFilter IS NULL OR Itemcode = @itemcodeFilter)
               AND (@varianceOnly = 0 OR Variance <> 0)";

    private static object BuildFilterParams(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        string? brandSearch, string? itemcodeSearch, bool varianceOnly) => new
    {
        countries = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noCountryFilter = countries is null ? 1 : 0,
        divisions = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDivisionFilter = divisions is null ? 1 : 0,
        departments = departments?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDepartmentFilter = departments is null ? 1 : 0,
        brandFilter = string.IsNullOrWhiteSpace(brandSearch) ? null : brandSearch.Trim(),
        itemcodeFilter = string.IsNullOrWhiteSpace(itemcodeSearch) ? null : itemcodeSearch.Trim(),
        varianceOnly = varianceOnly ? 1 : 0,
        excludedDivisions = ExcludedDivisions,
    };

    /// <summary>Row count + sums over the WHOLE filtered set, for the totals row and
    /// the pager's "N rows / M pages" display.</summary>
    public async Task<EcomStockVarianceTotals> GetTotalsAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        string? brandSearch, string? itemcodeSearch, bool varianceOnly, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var totals = await c.QuerySingleAsync<EcomStockVarianceTotals>(new CommandDefinition($@"
            SELECT COUNT(*) AS [RowCount],
                   ISNULL(SUM(CAST(IncreffSOH AS BIGINT)), 0) AS IncreffSOH,
                   ISNULL(SUM(CAST(MFCS_SOH AS BIGINT)), 0)   AS MFCS_SOH,
                   ISNULL(SUM(CAST(GateKeeperRejectedSummer AS BIGINT)), 0) AS GateKeeperRejectedSummer,
                   ISNULL(SUM(CAST(GateKeeperRejectedWinter AS BIGINT)), 0) AS GateKeeperRejectedWinter,
                   ISNULL(SUM(CAST(Variance AS BIGINT)), 0)   AS Variance,
                   ISNULL(SUM(CAST(InTransitUAE AS BIGINT)), 0) AS InTransitUAE,
                   ISNULL(SUM(CAST(InTransitKSA AS BIGINT)), 0) AS InTransitKSA
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql};",
            BuildFilterParams(countries, divisions, departments, brandSearch, itemcodeSearch, varianceOnly),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return totals;
    }

    /// <summary>One page (PageSize rows) of the filtered set, 1-indexed.</summary>
    public async Task<List<EcomStockVarianceRow>> GetReportPageAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        string? brandSearch, string? itemcodeSearch, bool varianceOnly, int pageNumber, CancellationToken ct = default)
    {
        var offset = Math.Max(0, pageNumber - 1) * PageSize;
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH,
                   GateKeeperRejectedSummer, GateKeeperRejectedWinter, Variance,
                   InTransitUAE, InTransitKSA, CreateTS,
                   {ClassificationSelectSql}, Brand
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
                departments = departments?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
                noDepartmentFilter = departments is null ? 1 : 0,
                brandFilter = string.IsNullOrWhiteSpace(brandSearch) ? null : brandSearch.Trim(),
                itemcodeFilter = string.IsNullOrWhiteSpace(itemcodeSearch) ? null : itemcodeSearch.Trim(),
                varianceOnly = varianceOnly ? 1 : 0,
                excludedDivisions = ExcludedDivisions,
                offset,
                pageSize = PageSize,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Unpaged — every row in the filtered set. Used only by Excel export,
    /// a one-off user-initiated action; not used for the on-screen grid.</summary>
    public async Task<List<EcomStockVarianceRow>> GetReportAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        string? brandSearch, string? itemcodeSearch, bool varianceOnly, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH,
                   GateKeeperRejectedSummer, GateKeeperRejectedWinter, Variance,
                   InTransitUAE, InTransitKSA, CreateTS,
                   {ClassificationSelectSql}, Brand
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql}
             ORDER BY Country, Itemcode;",
            BuildFilterParams(countries, divisions, departments, brandSearch, itemcodeSearch, varianceOnly),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Division-level (Country + Division) roll-up of the filtered set — small
    /// enough (dozens of rows) that it's loaded whole, no paging. The Itemcode filter
    /// doesn't apply here (there's no Itemcode input on the summary tab).</summary>
    public async Task<List<EcomStockVarianceDivisionSummaryRow>> GetDivisionSummaryAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        string? brandSearch, bool varianceOnly, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceDivisionSummaryRow>(new CommandDefinition($@"
            SELECT Country,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Division END AS Division,
                   COUNT(*) AS ItemCount,
                   ISNULL(SUM(CAST(IncreffSOH AS BIGINT)), 0) AS IncreffSOH,
                   ISNULL(SUM(CAST(MFCS_SOH AS BIGINT)), 0)   AS MFCS_SOH,
                   ISNULL(SUM(CAST(GateKeeperRejectedSummer AS BIGINT)), 0) AS GateKeeperRejectedSummer,
                   ISNULL(SUM(CAST(GateKeeperRejectedWinter AS BIGINT)), 0) AS GateKeeperRejectedWinter,
                   ISNULL(SUM(CAST(Variance AS BIGINT)), 0)   AS Variance,
                   ISNULL(SUM(CAST(InTransitUAE AS BIGINT)), 0) AS InTransitUAE,
                   ISNULL(SUM(CAST(InTransitKSA AS BIGINT)), 0) AS InTransitKSA
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql}
             GROUP BY Country, CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Division END
             ORDER BY Country, Division;",
            BuildFilterParams(countries, divisions, departments, brandSearch, null, varianceOnly),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Whole-table KPI snapshot for the Dashboard tab. Deliberately unfiltered —
    /// no Division/Department/Brand/Itemcode/Variance-only, unlike every other method
    /// here. <paramref name="country"/> is the one exception: null gives the combined
    /// UAE+KSA total (the tab's primary view); passing "UAE" or "KSA" gives the
    /// per-country breakdown shown below it.</summary>
    public async Task<EcomStockVarianceDashboardSummary> GetDashboardSummaryAsync(string? country = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.QuerySingleAsync<EcomStockVarianceDashboardSummary>(new CommandDefinition(@"
            SELECT
                ISNULL(SUM(CAST(MFCS_SOH AS BIGINT)), 0)      AS MfcsSoh,
                ISNULL(SUM(CAST(IncreffSOH AS BIGINT)), 0)    AS IncreffSoh,
                ISNULL(SUM(CAST(Variance AS BIGINT)), 0)      AS NetVariance,
                ISNULL(SUM(CAST(ABS(Variance) AS BIGINT)), 0) AS GrossGap,
                SUM(CASE WHEN Variance <> 0 THEN 1 ELSE 0 END) AS VarianceSkuCount,
                SUM(CASE WHEN MFCS_SOH > 0 THEN 1 ELSE 0 END)  AS PositiveStockSkuCount,
                SUM(CASE WHEN MFCS_SOH > 0 AND Variance = 0 THEN 1 ELSE 0 END) AS ExactMatchPositiveStockSkuCount,
                COUNT(*) AS RowsReviewed
              FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE @country IS NULL OR Country = @country;",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // Fixed display order, independent of whatever order SQL Server happens to return
    // groups in — a bucket with zero rows still gets a row (with zeros) rather than
    // disappearing from the table.
    private static readonly string[] ReconciliationBucketOrder =
    [
        "Unmapped / Blank Item Code",
        "Negative MFCS Inventory",
        "Exclusive to MFCS",
        "Exclusive to Increff",
        "Common - Exact Match",
        "Common - MFCS Higher",
        "Common - Increff Higher",
        "Both Zero / No Stock",
    ];

    private record BucketRaw(string Bucket, int SkuCount, long MfcsSoh, long IncreffSoh, long NetVariance);

    /// <summary>Dashboard's "Net Variance by Reconciliation Bucket" table — deliberately
    /// unfiltered, same as GetDashboardSummaryAsync (and same <paramref name="country"/>
    /// convention: null = combined UAE+KSA, "UAE"/"KSA" = per-country breakdown). Buckets
    /// partition every row by comparing MFCS_SOH against the GS/GW-adjusted Increff figure
    /// (see EcomStockVarianceReconciliationBucket's doc comment) — mutually exclusive and
    /// exhaustive, so the 8 rows' SkuCount/MfcsSoh/IncreffSoh/NetVariance sum exactly to
    /// GetDashboardSummaryAsync's RowsReviewed/MfcsSoh/(IncreffSoh+GS+GW total)/NetVariance
    /// for the same country. Each bucket's rows all share the same Variance sign by
    /// construction (e.g. every row in "Exclusive to MFCS" has Variance = MFCS_SOH > 0), so
    /// ABS(bucket NetVariance) sums exactly to GetDashboardSummaryAsync's GrossGap too —
    /// that's what GrossGapSharePercent is a share of.</summary>
    public async Task<List<EcomStockVarianceReconciliationBucket>> GetReconciliationBucketsAsync(string? country = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var raw = (await c.QueryAsync<BucketRaw>(new CommandDefinition(@"
            ;WITH Base AS (
                SELECT MFCS_SOH, Variance, Itemcode,
                       (IncreffSOH + GateKeeperRejectedSummer + GateKeeperRejectedWinter) AS EffectiveIncreffSoh
                  FROM dbo.LPM_ECOM_SOH_COMPARISON
                 WHERE @country IS NULL OR Country = @country
            ),
            Bucketed AS (
                SELECT
                    CASE
                        WHEN Itemcode IS NULL OR LTRIM(RTRIM(Itemcode)) = '' THEN 'Unmapped / Blank Item Code'
                        WHEN MFCS_SOH < 0 THEN 'Negative MFCS Inventory'
                        WHEN MFCS_SOH > 0 AND EffectiveIncreffSoh = 0 THEN 'Exclusive to MFCS'
                        WHEN MFCS_SOH = 0 AND EffectiveIncreffSoh > 0 THEN 'Exclusive to Increff'
                        WHEN MFCS_SOH = EffectiveIncreffSoh AND MFCS_SOH > 0 THEN 'Common - Exact Match'
                        WHEN MFCS_SOH > EffectiveIncreffSoh THEN 'Common - MFCS Higher'
                        WHEN MFCS_SOH < EffectiveIncreffSoh THEN 'Common - Increff Higher'
                        ELSE 'Both Zero / No Stock'
                    END AS Bucket,
                    MFCS_SOH, EffectiveIncreffSoh, Variance
                  FROM Base
            )
            SELECT Bucket,
                   COUNT(*) AS SkuCount,
                   SUM(CAST(MFCS_SOH AS BIGINT)) AS MfcsSoh,
                   SUM(CAST(EffectiveIncreffSoh AS BIGINT)) AS IncreffSoh,
                   SUM(CAST(Variance AS BIGINT)) AS NetVariance
              FROM Bucketed
             GROUP BY Bucket;",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var totalGrossGap = raw.Sum(r => Math.Abs(r.NetVariance));

        return ReconciliationBucketOrder
            .Select(name =>
            {
                var r = raw.FirstOrDefault(x => x.Bucket == name);
                var netVariance = r?.NetVariance ?? 0;
                var share = totalGrossGap == 0 ? 0 : Math.Abs(netVariance) * 100.0 / totalGrossGap;
                return new EcomStockVarianceReconciliationBucket(
                    name, r?.SkuCount ?? 0, r?.MfcsSoh ?? 0, r?.IncreffSoh ?? 0, netVariance, share);
            })
            .ToList();
    }
}
