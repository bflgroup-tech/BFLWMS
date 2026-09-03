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
/// Two connections, not one: ONLINE only lives on WmsProductionDb (same as
/// TechnoBuildingService). CheckingAmountJafza was assumed reachable from
/// that same connection too, on the strength of ContainerAllocationDataSyncService
/// already reading/writing BFLDATA.dbo.RFIDTransfer from it — but that grant
/// turned out to be per-table, not database-wide: WmsProductionDb's login has
/// no SELECT on BFLDATA.dbo.CheckingAmountJafza (confirmed in production).
/// CheckingAmountJafza IS reachable from OnPremBackup, so the two counts are
/// fetched from their respective connections and joined in C#, the same
/// pattern TechnoPairingService uses for its MANUAL/AUTO split.
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

    private record CountRow(DateTime TrnDate, string EmpCode, int Cnt);
    private record AmountRow(DateTime Trndate, string EmpCode, decimal TotalAmount);

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

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    private const string CountSql = @"
        SELECT p.TrnDate, p.EmpCode,
               Cnt = SUM(ISNULL(p.HR0A,0)+ISNULL(p.HR1A,0)+ISNULL(p.HR2A,0)+ISNULL(p.HR3A,0)+ISNULL(p.HR4A,0)+
                         ISNULL(p.HR5A,0)+ISNULL(p.HR6A,0)+ISNULL(p.HR7A,0)+ISNULL(p.HR8A,0)+ISNULL(p.HR9A,0)+
                         ISNULL(p.HR10A,0)+ISNULL(p.HR11A,0)+ISNULL(p.HR12A,0)+ISNULL(p.HR13A,0)+ISNULL(p.HR14A,0)+
                         ISNULL(p.HR15A,0)+ISNULL(p.HR16A,0)+ISNULL(p.HR17A,0)+ISNULL(p.HR18A,0)+ISNULL(p.HR19A,0)+
                         ISNULL(p.HR20A,0)+ISNULL(p.HR21A,0)+ISNULL(p.HR22A,0))
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild p
         WHERE p.[Type] = 'JCM' AND p.Warehouse = 'JAFZA'
           AND p.TrnDate >= @fromDate AND p.TrnDate <= @toDate
           AND (@empCodeFilter IS NULL OR p.EmpCode = @empCodeFilter)
         GROUP BY p.TrnDate, p.EmpCode
         ORDER BY p.TrnDate, p.EmpCode;";

    private const string AmountSql = @"
        SELECT Trndate, EmpCode, TotalAmount = SUM(Amount)
          FROM BFLDATA.dbo.CheckingAmountJafza
         WHERE CheckType = 'AUTO'
           AND Trndate >= @fromDate AND Trndate <= @toDate
           AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
         GROUP BY EmpCode, Trndate;";

    public async Task<List<JafzaExportCheckingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, string? empCodeSearch, CancellationToken ct = default)
    {
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        List<CountRow> counts;
        await using (var c = OpenWmsProductionDb())
        {
            counts = (await c.QueryAsync<CountRow>(new CommandDefinition(
                CountSql, new { fromDate, toDate, empCodeFilter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }
        if (counts.Count == 0) return new();

        List<AmountRow> amounts;
        await using (var c = OpenOnPremBackup())
        {
            amounts = (await c.QueryAsync<AmountRow>(new CommandDefinition(
                AmountSql, new { fromDate, toDate, empCodeFilter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        var amountLookup = amounts.ToDictionary(a => (a.Trndate, a.EmpCode), a => a.TotalAmount);

        return counts
            .Select(c => new JafzaExportCheckingRow(
                c.TrnDate, c.EmpCode, c.Cnt,
                amountLookup.GetValueOrDefault((c.TrnDate, c.EmpCode), 0m)))
            .ToList();
    }
}
