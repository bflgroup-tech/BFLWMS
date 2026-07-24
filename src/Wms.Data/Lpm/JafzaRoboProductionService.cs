using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Robo Production Report. Ported from a legacy VB.NET desktop query
/// ("#pair") against ROBOTICS.dbo.PairingConformationDetail.
///
/// Two-connection split (confirmed against the real environment): only
/// ROBOTICS.dbo.PairingConformationDetail itself is read via the JafazaRoboDb
/// connection (same one the Chute Mapping page uses) — that server is
/// dedicated to ROBOTICS.dbo.* and doesn't have USA/hodata/FABSMAIN/PAYROLL
/// ("Invalid object name" for all four when queried there). Every enrichment
/// lookup instead runs on the normal OnPremBackupDB (LOGBACKUP) connection,
/// same as every other report in this app.
///
/// Legacy logic, preserved as closely as possible:
///   1) One row per (TrnDate, ItemCode, EmpCode/username), Qty = COUNT(*).
///      Scans before 05:00 belong to the previous day (TrnDate -= 1 when
///      LEFT(TrnTime,2) IN ('00'..'04')) — same shift-boundary rule as the
///      Manual report — folded into the same GROUP BY so TrnTime itself
///      doesn't need to survive past this query.
///   2) EmpName: 'ROBO%' logins display as-is; everything else resolves via
///      FABSMAIN.dbo.[user] (UserName -> RecStartingNo). The legacy
///      PAYROLL.dbo.Employee follow-up lookup (RecStartingNo -> EmpName)
///      is NOT ported — PAYROLL doesn't exist on this SQL Server instance
///      at all (confirmed), so RecStartingNo is shown as-is.
///   3) GroupCode: USA.dbo.UPCBarCodes.GroupCode via ItemCode. The legacy
///      hodata.dbo.itemmaster lookup (tried first) is NOT ported — no
///      practical difference observed once resolved against LOGBACKUP.
///   4) Division: USA.dbo.USAPriority.DivisionY via GroupCode — resolved via
///      a C# dictionary (last-write-wins) rather than the legacy inline
///      scalar subquery, which throws "Subquery returned more than 1 value"
///      if a GroupCode maps to more than one division row.
///   5) GroupName (Detailed only): hodata.dbo.itemgroup.Description via GroupCode.
/// </summary>
public class JafzaRoboProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;
    private const string RoboConnectionStringKey = "JafazaRoboDb";

    private SqlConnection OpenRobo()
    {
        var c = new SqlConnection(resolver.GetRoboticsConnectionString(RoboConnectionStringKey));
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private record RawPairRow(DateTime TrnDate, string ItemCode, string EmpCode, int Qty);

    private const string RawQuerySql = @"
        SELECT
            TrnDate = CASE WHEN LEFT(TrnTime, 2) IN ('00','01','02','03','04') THEN DATEADD(day, -1, TrnDate) ELSE TrnDate END,
            ItemCode = itemcode,
            EmpCode  = username,
            Qty      = COUNT(*)
          FROM ROBOTICS.dbo.PairingConformationDetail
         WHERE TrnDate BETWEEN @from AND @to
           AND (@username IS NULL OR username = @username)
         GROUP BY CASE WHEN LEFT(TrnTime, 2) IN ('00','01','02','03','04') THEN DATEADD(day, -1, TrnDate) ELSE TrnDate END,
                  itemcode, username";

    private async Task<List<RawPairRow>> FetchRawAsync(
        DateTime fromDate, DateTime toDate, string? usernameFilter, CancellationToken ct)
    {
        await using var c = OpenRobo();
        var rows = await c.QueryAsync<RawPairRow>(new CommandDefinition(
            RawQuerySql, new { from = fromDate.Date, to = toDate.Date, username = usernameFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private record EnrichmentLookups(
        Dictionary<string, string?> EmpNameByCode,
        Dictionary<string, string?> GroupByItem,
        Dictionary<string, string?> DivByGroup,
        Dictionary<string, string?> GroupNameByGroup);

    private async Task<EnrichmentLookups> EnrichAsync(IReadOnlyList<RawPairRow> raw, CancellationToken ct)
    {
        var empNameByCode  = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var groupByItem    = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var divByGroup     = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var groupNameByGroup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var nonRoboCodes = raw.Select(r => r.EmpCode)
            .Where(e => !string.IsNullOrWhiteSpace(e) && !e.StartsWith("ROBO", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToArray();
        var itemCodes = raw.Select(r => r.ItemCode).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray();
        if (nonRoboCodes.Length == 0 && itemCodes.Length == 0)
            return new EnrichmentLookups(empNameByCode, groupByItem, divByGroup, groupNameByGroup);

        await using var c = OpenOnPremBackup();

        if (nonRoboCodes.Length > 0)
        {
            var empRows = await c.QueryAsync<(string UserName, string? RecStartingNo)>(new CommandDefinition(@"
                SELECT UserName, RecStartingNo
                  FROM FABSMAIN.dbo.[user]
                 WHERE UserName IN @codes",
                new { codes = nonRoboCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in empRows)
                if (!string.IsNullOrWhiteSpace(r.RecStartingNo)) empNameByCode[r.UserName] = r.RecStartingNo;
        }

        if (itemCodes.Length > 0)
        {
            var groupRows = await c.QueryAsync<(string Itemcode, string? GroupCode)>(new CommandDefinition(@"
                SELECT Itemcode, GroupCode = MAX(GroupCode)
                  FROM USA.dbo.UPCBarCodes
                 WHERE Itemcode IN @codes
                 GROUP BY Itemcode",
                new { codes = itemCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in groupRows) groupByItem[r.Itemcode] = r.GroupCode;

            var groupCodes = groupByItem.Values.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToArray()!;
            if (groupCodes.Length > 0)
            {
                var divRows = await c.QueryAsync<(string GroupCode, string? DivisionY)>(new CommandDefinition(@"
                    SELECT DISTINCT GroupCode, DivisionY
                      FROM USA.dbo.USAPriority
                     WHERE GroupCode IN @codes",
                    new { codes = groupCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in divRows) divByGroup[r.GroupCode] = r.DivisionY;

                var nameRows = await c.QueryAsync<(string GroupCode, string? Description)>(new CommandDefinition(@"
                    SELECT GroupCode, Description
                      FROM hodata.dbo.itemgroup
                     WHERE GroupCode IN @codes",
                    new { codes = groupCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in nameRows) groupNameByGroup[r.GroupCode] = r.Description;
            }
        }

        return new EnrichmentLookups(empNameByCode, groupByItem, divByGroup, groupNameByGroup);
    }

    private static string ResolveUsername(RawPairRow r, EnrichmentLookups lk) =>
        r.EmpCode.StartsWith("ROBO", StringComparison.OrdinalIgnoreCase)
            ? r.EmpCode
            : lk.EmpNameByCode.GetValueOrDefault(r.EmpCode) ?? r.EmpCode;

    /// <summary>Division-wise summary — one row per (TrnDate, Division, Username).</summary>
    public async Task<List<JafzaRoboProductionSummaryRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        var raw = await FetchRawAsync(fromDate, toDate, usernameFilter, ct);
        var lk = await EnrichAsync(raw, ct);

        return raw
            .Select(r => new
            {
                r.TrnDate,
                r.Qty,
                Username = ResolveUsername(r, lk),
                Division = lk.DivByGroup.GetValueOrDefault(lk.GroupByItem.GetValueOrDefault(r.ItemCode) ?? "")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Division))
            .GroupBy(x => (x.TrnDate, x.Division, x.Username))
            .Select(g => new JafzaRoboProductionSummaryRow(
                TrnDate:  g.Key.TrnDate,
                Division: g.Key.Division!,
                Username: g.Key.Username,
                Qty:      g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.Division).ThenBy(r => r.Username)
            .ToList();
    }

    /// <summary>Item-wise detail — one row per (TrnDate, ItemCode, Username, GroupCode).</summary>
    public async Task<List<JafzaRoboProductionDetailRow>> GetDetailAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        var raw = await FetchRawAsync(fromDate, toDate, usernameFilter, ct);
        var lk = await EnrichAsync(raw, ct);

        return raw
            .Select(r =>
            {
                var groupCode = lk.GroupByItem.GetValueOrDefault(r.ItemCode);
                return new
                {
                    r.TrnDate,
                    r.ItemCode,
                    r.Qty,
                    Username  = ResolveUsername(r, lk),
                    GroupCode = groupCode,
                    GroupName = string.IsNullOrWhiteSpace(groupCode) ? null : lk.GroupNameByGroup.GetValueOrDefault(groupCode),
                    Division  = string.IsNullOrWhiteSpace(groupCode) ? null : lk.DivByGroup.GetValueOrDefault(groupCode)
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Division))
            .GroupBy(x => (x.TrnDate, x.ItemCode, x.Username, x.GroupCode, x.GroupName, x.Division))
            .Select(g => new JafzaRoboProductionDetailRow(
                TrnDate:   g.Key.TrnDate,
                ItemCode:  g.Key.ItemCode,
                Username:  g.Key.Username,
                GroupCode: g.Key.GroupCode,
                GroupName: g.Key.GroupName,
                Division:  g.Key.Division!,
                Qty:       g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.ItemCode)
            .ToList();
    }
}
