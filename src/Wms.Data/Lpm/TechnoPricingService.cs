using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record TechnoPricingRow(
    string Type, DateTime TrnDate, string EmpCode, string? EmpName, string? GroupName,
    string? Division, int Qty, decimal? Multiplier);

/// <summary>
/// TECHNO pricing-incentive counts — one row per (TrnDate, EmpCode, GroupName,
/// Multiplier), Qty = summed hourly pricing counts for that employee/day/group.
/// Adapted from an ad-hoc SQL report query.
///
/// Source: BFLDATA.dbo.PricingCount via OnPremBackupDB_ConnectionString — same
/// on-prem instance as the other tabs. Unlike Checking/Pairing/Building, this
/// table carries EmpName and GroupName directly, so no PAYROLL/FABSMAIN lookup
/// gap applies here.
///
/// PricingCount has NO warehouse column at all, so unlike the other tabs this
/// can't be filtered to TECHNO specifically — it shows whatever is in the
/// table. Not confirmed whether other warehouses ever write to it too.
///
/// Division is resolved per GroupCode via bfldata.dbo.DeptStock, matched
/// through usa.dbo.usapriority's Department — TOP 1 defensively (verified
/// against live data that no GroupCode currently maps to more than one
/// Division, but a plain scalar subquery would throw if that ever changed).
///
/// As of this writing the table's last row is dated 2026-06-17 — it may not
/// be actively written to anymore, so a default "last 7 days" range will
/// commonly show zero rows.
/// </summary>
public class TechnoPricingService(IOnPremConnectionResolver resolver)
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

    /// <summary>Division list for the filter — every distinct Division in
    /// bfldata.dbo.DeptStock, the same table this report joins to.</summary>
    public async Task<List<string>> GetDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Division FROM bfldata.dbo.DeptStock
             WHERE Division IS NOT NULL AND Division <> ''
             ORDER BY Division",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private const string ReportSql = @"
        ;WITH Agg AS (
            SELECT a.TrnDate, a.EmpCode, a.EmpName, a.GroupName,
                   Division = (SELECT TOP 1 Division FROM bfldata.dbo.DeptStock
                                 WHERE Department IN (
                                     SELECT Department FROM usa.dbo.usapriority WHERE groupCode = a.GroupCode)),
                   Qty = SUM(ISNULL(Ch1,0)+ISNULL(Ch2,0)+ISNULL(Ch3,0)+ISNULL(Ch4,0)+ISNULL(Ch5,0)+
                             ISNULL(Ch6,0)+ISNULL(Ch7,0)+ISNULL(Ch8,0)+ISNULL(Ch9,0)+ISNULL(Ch10,0)+
                             ISNULL(Ch11,0)+ISNULL(Ch12,0)+ISNULL(Ch13,0)+ISNULL(Ch14,0)+ISNULL(Ch15,0)+
                             ISNULL(Ch16,0)+ISNULL(Ch17,0)+ISNULL(Ch18,0)+ISNULL(CH19,0)+ISNULL(Ch20,0)+
                             ISNULL(Ch21,0)+ISNULL(Ch22,0)+ISNULL(Ch0,0)),
                   a.Multiplier
              FROM BFLDATA.dbo.PricingCount a
             WHERE a.TrnDate >= @fromDate AND a.TrnDate <= @toDate
             GROUP BY a.TrnDate, a.EmpCode, a.EmpName, a.GroupName, a.GroupCode, a.Multiplier
        )
        SELECT Type = 'PRICING', TrnDate, EmpCode, EmpName, GroupName, Division, Qty, Multiplier
          FROM Agg
         WHERE (@noDivisionFilter = 1 OR Division IN @divisions)
           AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
         ORDER BY TrnDate, EmpCode;";

    public async Task<List<TechnoPricingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, IEnumerable<string>? divisions,
        string? empCodeSearch, CancellationToken ct = default)
    {
        var divisionList = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noDivisionFilter = divisions is null;
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<TechnoPricingRow>(new CommandDefinition(
            ReportSql,
            new
            {
                fromDate, toDate,
                divisions = divisionList, noDivisionFilter = noDivisionFilter ? 1 : 0,
                empCodeFilter,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
