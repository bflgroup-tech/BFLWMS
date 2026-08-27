using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record WarehouseIncentiveRow(
    string Country, string Warehouse, string Type, string Area,
    DateTime TrnDate, string EmpCode, string? Division, int Qty);

/// <summary>
/// UAE/TECHNO checking-incentive counts — one row per (Area, TrnDate, EmpCode,
/// Division), Qty = how many items that employee checked. Area is AUTO when the
/// checking company name starts with "ROBO", MANUAL otherwise. Division comes
/// from DATAREPORTING.dbo.vUPC_SUBCLASS (deduped to one row per Itemcode via
/// ROW_NUMBER — that view has a handful of duplicate Itemcode rows, same as
/// IncreffMfcsSohCompareService).
///
/// Sources: USA.dbo.AMEChecking and BFLDATA.dbo.JAFZAChecking, both reached via
/// OnPremBackupDB_ConnectionString (same on-prem instance as LPMSIM/DATAREPORTING).
/// A checking row with Time1 hour 00-04 is credited to the PREVIOUS calendar day
/// (a late-night scan belongs to the shift that started the day before).
///
/// EmpName (from PAYROLL.dbo.employee in the original ad-hoc query this was built
/// from) is NOT included — PAYROLL isn't reachable from this app's connections,
/// same limitation already hit and accepted in JafzaDivisionProductionService/
/// JafzaRoboProductionService. EmpCode is shown as-is.
///
/// Country/Warehouse/Type are fixed literals ('UAE'/'TECHNO'/'CHECKING') matching
/// the source query — not real filterable columns, so there's no country/warehouse
/// filter here.
/// </summary>
public class WarehouseIncentivesService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    // A week of checking data takes ~10s; a full month can take well over a
    // minute (1.7M+ raw rows before aggregation) — same generous budget as the
    // ECOM Stock Variance Report's on-prem queries.
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

    public async Task<List<WarehouseIncentiveRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, IEnumerable<string>? areas, IEnumerable<string>? divisions,
        string? empCodeSearch, CancellationToken ct = default)
    {
        var areaList = areas?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noAreaFilter = areas is null;
        var divisionList = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noDivisionFilter = divisions is null;
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<WarehouseIncentiveRow>(new CommandDefinition(@"
            ;WITH Base AS (
                SELECT TrnDate, Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode) AS Qty
                  FROM USA.dbo.AMEChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) NOT IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT TrnDate, Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM BFLDATA.dbo.JAFZAChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) NOT IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT DATEADD(day, -1, TrnDate), Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM USA.dbo.AMEChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT DATEADD(day, -1, TrnDate), Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM BFLDATA.dbo.JAFZAChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
            ),
            Filtered AS (
                SELECT * FROM Base WHERE ISNULL(Itemcode, '') <> ''
            ),
            Subclass AS (
                SELECT itemcode, Division,
                       ROW_NUMBER() OVER (PARTITION BY itemcode ORDER BY (SELECT NULL)) AS rn
                  FROM DATAREPORTING.dbo.vUPC_SUBCLASS
            ),
            Enriched AS (
                SELECT f.TrnDate, f.EmpCode, f.Qty, s.Division,
                       Area = CASE WHEN f.CmpName LIKE 'ROBO%' THEN 'AUTO' ELSE 'MANUAL' END
                  FROM Filtered f
                  LEFT JOIN Subclass s ON s.itemcode = f.Itemcode AND s.rn = 1
            )
            SELECT Country = 'UAE', Warehouse = 'TECHNO', Type = 'CHECKING', Area, TrnDate, EmpCode, Division,
                   Qty = SUM(Qty)
              FROM Enriched
             WHERE (@noAreaFilter = 1 OR Area IN @areas)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
             GROUP BY Area, TrnDate, EmpCode, Division
             ORDER BY TrnDate, EmpCode;",
            new
            {
                fromDate, toDate,
                areas = areaList, noAreaFilter = noAreaFilter ? 1 : 0,
                divisions = divisionList, noDivisionFilter = noDivisionFilter ? 1 : 0,
                empCodeFilter,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
