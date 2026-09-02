using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record TechnoBuildingRow(string Type, string Area, DateTime TrnDate, string EmpCode, int Qty);

/// <summary>
/// TECHNO building-incentive counts — one row per (Area, TrnDate, EmpCode),
/// Qty = summed hourly build counts for that employee/day. Adapted from an
/// ad-hoc VB.NET report query.
///
/// Source: ONLINE.dbo.RFPairingCountPhotoCheckBuild on WmsProductionDb — the
/// same on-prem "legacy R1" server already used by
/// ContainerAllocationDataSyncService for online.dbo.PhotoCheckingResult.
/// Type 'PB' -> AUTO, everything else ('MB') -> MANUAL.
///
/// The source table stores one row per employee/day with 24 hourly count
/// columns (HR0A..HR23A). A shift spanning midnight logs its 18:00-23:59
/// hours under the FOLLOWING calendar date, so those hours are pulled back
/// onto the day the shift started: for report date D, Qty = (hours 0-17 of
/// the row dated D) + (hours 18-23 of the row dated D+1). The two halves are
/// disjoint (0-17 vs 18-23), so unlike the Pairing report's original query,
/// there's no double-counting bug here to fix.
///
/// EmpName (PAYROLL.dbo.employee in the original query) and the EmpCode
/// remap via FABSMAIN..[user] (INC-30928) are NOT included — FABSMAIN and
/// PAYROLL are unreachable from OnPremBackup's login (confirmed earlier in
/// this codebase); reachability from WmsProductionDb's own login hasn't been
/// separately verified, so this assumes the same gap applies and shows the
/// raw EmpCode as-is, matching the precedent in WarehouseIncentivesService /
/// TechnoPairingService.
/// </summary>
public class TechnoBuildingService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsProductionDbConnectionString()));
        c.Open();
        return c;
    }

    private const string ReportSql = @"
        IF OBJECT_ID('tempdb..#BuildCountTemp') IS NOT NULL DROP TABLE #BuildCountTemp;

        CREATE TABLE #BuildCountTemp (
            EmpCode VARCHAR(50), Area VARCHAR(10), TrnDate DATETIME,
            HR0A INT, HR1A INT, HR2A INT, HR3A INT, HR4A INT, HR5A INT, HR6A INT, HR7A INT,
            HR8A INT, HR9A INT, HR10A INT, HR11A INT, HR12A INT, HR13A INT, HR14A INT,
            HR15A INT, HR16A INT, HR17A INT, HR18A INT, HR19A INT, HR20A INT, HR21A INT,
            HR22A INT, HR23A INT
        );

        INSERT INTO #BuildCountTemp
        SELECT EmpCode, CASE WHEN Type = 'PB' THEN 'AUTO' ELSE 'MANUAL' END, TrnDate,
               HR0A, HR1A, HR2A, HR3A, HR4A, HR5A, HR6A, HR7A, HR8A, HR9A, HR10A, HR11A,
               HR12A, HR13A, HR14A, HR15A, HR16A, HR17A, 0, 0, 0, 0, 0, 0
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild
         WHERE WareHouse = 'TECHNO' AND Type IN ('PB','MB')
           AND TrnDate >= @fromDate AND TrnDate <= @toDate;

        INSERT INTO #BuildCountTemp
        SELECT EmpCode, CASE WHEN Type = 'PB' THEN 'AUTO' ELSE 'MANUAL' END, DATEADD(day, -1, TrnDate),
               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
               HR18A, HR19A, HR20A, HR21A, HR22A, HR23A
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild
         WHERE WareHouse = 'TECHNO' AND Type IN ('PB','MB')
           AND TrnDate > @fromDate AND TrnDate <= DATEADD(day, 1, @toDate);

        SELECT Type = 'BUILDING', Area, TrnDate, EmpCode,
               Qty = SUM(ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                         ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                         ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                         ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                         ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)+ISNULL(HR23A,0))
          FROM #BuildCountTemp
         WHERE (@noAreaFilter = 1 OR Area IN @areas)
           AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
         GROUP BY Area, TrnDate, EmpCode
         ORDER BY TrnDate, EmpCode;";

    public async Task<List<TechnoBuildingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, IEnumerable<string>? areas,
        string? empCodeSearch, CancellationToken ct = default)
    {
        var areaList = areas?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noAreaFilter = areas is null;
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        await using var c = OpenWmsProductionDb();
        var rows = await c.QueryAsync<TechnoBuildingRow>(new CommandDefinition(
            ReportSql,
            new
            {
                fromDate, toDate,
                areas = areaList, noAreaFilter = noAreaFilter ? 1 : 0,
                empCodeFilter,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
