using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record JafzaExportCheckingRow(DateTime TrnDate, string EmpCode, int Cnt, decimal IncentiveAmount);

/// <summary>
/// JAFZA export-checking counts per employee per day, from
/// ONLINE.dbo.RFPairingCountPhotoCheckBuild (Type='JCM', Warehouse='JAFZA'),
/// enriched with a per-day incentive amount from BFLDATA.dbo.CheckingAmountJafza
/// (CheckType='AUTO'). Adapted from an ad-hoc SQL report.
///
/// Both tables are queried through WmsProductionDb: ONLINE only lives there
/// (same as TechnoBuildingService), and that connection's login is already
/// confirmed to reach BFLDATA.dbo.* cross-database (see
/// ContainerAllocationDataSyncService's use of BFLDATA.dbo.RFIDTransfer from
/// this same connection) — CheckingAmountJafza itself wasn't independently
/// re-verified since it's not reachable from this app's other on-prem
/// connection to test against, but it's the same schema/login.
///
/// The original ad-hoc query enriched via UPDATE #temp ... FROM #temp a,
/// CheckingAmountJafza b WHERE a.EmpCode=b.EmpCode AND a.TrnDate=b.TrnDate —
/// a one-to-many join if more than one CheckType='AUTO' row exists for the
/// same EmpCode+TrnDate, which SQL Server resolves non-deterministically
/// (silently keeping an arbitrary matching row's Amount). Verified live: one
/// such duplicate exists today (EmpCode 5161, 2023-05-08). This instead
/// SUMs Amount per EmpCode+TrnDate before joining, at the user's request.
///
/// CheckingAmountJafza's most recent row is dated 2025-05-07 — IncentiveAmount
/// will read 0 for any more recent date, same staleness pattern already seen
/// in TechnoPricingService's source table.
///
/// Sums HR0A through HR22A only (23 columns) — HR23A is deliberately excluded,
/// matching the original query exactly (unlike TechnoBuildingService's source
/// use, which sums all 24 HR0A..HR23A columns for the Building tab).
/// </summary>
public class JafzaExportCheckingService(IOnPremConnectionResolver resolver)
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
        SELECT p.TrnDate, p.EmpCode,
               Cnt = SUM(ISNULL(p.HR0A,0)+ISNULL(p.HR1A,0)+ISNULL(p.HR2A,0)+ISNULL(p.HR3A,0)+ISNULL(p.HR4A,0)+
                         ISNULL(p.HR5A,0)+ISNULL(p.HR6A,0)+ISNULL(p.HR7A,0)+ISNULL(p.HR8A,0)+ISNULL(p.HR9A,0)+
                         ISNULL(p.HR10A,0)+ISNULL(p.HR11A,0)+ISNULL(p.HR12A,0)+ISNULL(p.HR13A,0)+ISNULL(p.HR14A,0)+
                         ISNULL(p.HR15A,0)+ISNULL(p.HR16A,0)+ISNULL(p.HR17A,0)+ISNULL(p.HR18A,0)+ISNULL(p.HR19A,0)+
                         ISNULL(p.HR20A,0)+ISNULL(p.HR21A,0)+ISNULL(p.HR22A,0)),
               IncentiveAmount = ISNULL(ca.TotalAmount, 0)
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild p
          LEFT JOIN (
              SELECT EmpCode, Trndate, TotalAmount = SUM(Amount)
                FROM BFLDATA.dbo.CheckingAmountJafza
               WHERE CheckType = 'AUTO'
               GROUP BY EmpCode, Trndate
          ) ca ON ca.EmpCode = p.EmpCode AND ca.Trndate = p.TrnDate
         WHERE p.[Type] = 'JCM' AND p.Warehouse = 'JAFZA'
           AND p.TrnDate >= @fromDate AND p.TrnDate <= @toDate
           AND (@empCodeFilter IS NULL OR p.EmpCode = @empCodeFilter)
         GROUP BY p.TrnDate, p.EmpCode, ca.TotalAmount
         ORDER BY p.TrnDate, p.EmpCode;";

    public async Task<List<JafzaExportCheckingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, string? empCodeSearch, CancellationToken ct = default)
    {
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        await using var c = OpenWmsProductionDb();
        var rows = await c.QueryAsync<JafzaExportCheckingRow>(new CommandDefinition(
            ReportSql, new { fromDate, toDate, empCodeFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
