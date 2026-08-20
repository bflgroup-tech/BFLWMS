using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record EcomStockVarianceRow(
    string Country, string Itemcode, int IncreffSOH, int MFCS_SOH, int Variance, DateTime CreateTS,
    string? Division, string? Department, string? Class, string? Subclass, string? Family);

/// <summary>
/// Backing service for the ECOM Stock Variance Report — reads
/// dbo.LPM_ECOM_SOH_COMPARISON directly. Division/Department/Class/Subclass/
/// Family are denormalized into that table at write time by
/// IncreffMfcsSohCompareService's Refresh Now (from DATAREPORTING.dbo.vUPC_SUBCLASS),
/// so this report no longer joins the 20M-row view itself at read time.
/// Filterable by Country and Division; no row cap — the page loads the full
/// matching set (up to 326K+ rows unfiltered) by design.
/// </summary>
public class EcomStockVarianceReportService(IOnPremConnectionResolver resolver)
{
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
               AND (@noDivisionFilter = 1 OR Division IN @divisions)";

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
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH, Variance, CreateTS,
                   Division, Department, Class, Subclass, Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON
            {FilterWhereSql}
             ORDER BY Country, Itemcode;",
            BuildFilterParams(countries, divisions),
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
