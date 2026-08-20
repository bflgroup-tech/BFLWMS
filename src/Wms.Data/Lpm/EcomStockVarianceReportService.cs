using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record EcomStockVarianceRow(
    string Country, string Itemcode, int IncreffSOH, int MFCS_SOH, int Variance, DateTime CreateTS,
    string? Division, string? Department, string? Class, string? Subclass, string? Family);

/// <summary>
/// Backing service for the ECOM Stock Variance Report — joins
/// dbo.LPM_ECOM_SOH_COMPARISON (populated by IncreffMfcsSohCompareService's
/// Refresh Now) to DATAREPORTING.dbo.vUPC_SUBCLASS on Itemcode, for the
/// Division/Department/Class/Subclass/Family breakdown. Filterable by
/// Country, Division, and Department — Division alone still leaves some
/// departments (e.g. Womenswear/Fast Fashion) over the page's row cap.
/// </summary>
public class EcomStockVarianceReportService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 60;

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

    /// <summary>Division list for the filter — every distinct Division in
    /// DATAREPORTING.dbo.vUPC_SUBCLASS, the same view this report joins to.</summary>
    public async Task<List<string>> GetDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Division FROM DATAREPORTING.dbo.vUPC_SUBCLASS
             WHERE Division IS NOT NULL AND Division <> ''
             ORDER BY Division",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>Department list for the filter — every distinct Department in
    /// DATAREPORTING.dbo.vUPC_SUBCLASS, the same view this report joins to.</summary>
    public async Task<List<string>> GetDepartmentsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Department FROM DATAREPORTING.dbo.vUPC_SUBCLASS
             WHERE Department IS NOT NULL AND Department <> ''
             ORDER BY Department",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // countries/divisions/departments: null means unrestricted; an empty-but-non-null
    // list means "match nothing" (a deny-by-default caller with zero country grants)
    // — same convention as ReportsService.GetPoCountingAsync.
    private const string FilterWhereSql = @"
             WHERE (@noCountryFilter = 1 OR a.Country IN @countries)
               AND (@noDivisionFilter = 1 OR b.Division IN @divisions)
               AND (@noDepartmentFilter = 1 OR b.Department IN @departments)";

    private static object BuildFilterParams(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments) => new
    {
        countries = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noCountryFilter = countries is null ? 1 : 0,
        divisions = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDivisionFilter = divisions is null ? 1 : 0,
        departments = departments?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDepartmentFilter = departments is null ? 1 : 0,
    };

    /// <summary>Cheap pre-check before GetReportAsync — the page uses this to refuse
    /// to render (and to avoid pulling the full row set over the wire) when the
    /// filtered result is too large for an unpaginated HTML table.</summary>
    public async Task<int> GetReportCountAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition($@"
            SELECT COUNT(*)
              FROM dbo.LPM_ECOM_SOH_COMPARISON a
              LEFT JOIN DATAREPORTING.dbo.vUPC_SUBCLASS b ON a.Itemcode = b.Itemcode
            {FilterWhereSql};",
            BuildFilterParams(countries, divisions, departments),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task<List<EcomStockVarianceRow>> GetReportAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, IEnumerable<string>? departments,
        CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT a.Country, a.Itemcode, a.IncreffSOH, a.MFCS_SOH, a.Variance, a.CreateTS,
                   b.Division, b.Department, b.class AS Class, b.subclass AS Subclass, b.Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON a
              LEFT JOIN DATAREPORTING.dbo.vUPC_SUBCLASS b ON a.Itemcode = b.Itemcode
            {FilterWhereSql}
             ORDER BY a.Country, a.Itemcode;",
            BuildFilterParams(countries, divisions, departments),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
