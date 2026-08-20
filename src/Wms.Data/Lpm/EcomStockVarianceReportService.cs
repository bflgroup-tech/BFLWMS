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
/// Country and Division; no row cap — the page loads the full matching set
/// (up to 326K+ rows unfiltered) by design.
/// </summary>
public class EcomStockVarianceReportService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    // Unfiltered load can be 300K+ rows against a 20M-row view join — the default
    // 60s used elsewhere in this file's siblings isn't enough headroom here.
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

    // countries/divisions: null means unrestricted; an empty-but-non-null list means
    // "match nothing" (a deny-by-default caller with zero country grants) — same
    // convention as ReportsService.GetPoCountingAsync.
    private const string FilterWhereSql = @"
             WHERE (@noCountryFilter = 1 OR a.Country IN @countries)
               AND (@noDivisionFilter = 1 OR b.Division IN @divisions)";

    private static object BuildFilterParams(IEnumerable<string>? countries, IEnumerable<string>? divisions) => new
    {
        countries = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noCountryFilter = countries is null ? 1 : 0,
        divisions = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>(),
        noDivisionFilter = divisions is null ? 1 : 0,
    };

    public async Task<List<EcomStockVarianceRow>> GetReportAsync(
        IEnumerable<string>? countries, IEnumerable<string>? divisions, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomStockVarianceRow>(new CommandDefinition($@"
            SELECT a.Country, a.Itemcode, a.IncreffSOH, a.MFCS_SOH, a.Variance, a.CreateTS,
                   b.Division, b.Department, b.class AS Class, b.subclass AS Subclass, b.Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON a
              LEFT JOIN DATAREPORTING.dbo.vUPC_SUBCLASS b ON a.Itemcode = b.Itemcode
            {FilterWhereSql}
             ORDER BY a.Country, a.Itemcode;",
            BuildFilterParams(countries, divisions),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
