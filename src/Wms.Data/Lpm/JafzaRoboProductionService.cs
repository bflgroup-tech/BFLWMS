using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Robo Production Report. Ported from a legacy VB.NET desktop query
/// ("#pair") against ROBOTICS.dbo.PairingConformationDetail. Both Summary and
/// Detailed read this same source — an alternate Summary source
/// (BFLDATA.dbo.DailyCountCategoryBuildRobo) was tried and dropped after its
/// totals were found to mismatch this connection's actual pairing data.
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
///   2) EmpName: not ported. 'ROBO%' logins display as-is; everything else
///      falls back to the raw empcode/username. The legacy FABSMAIN.dbo.[user]
///      and PAYROLL.dbo.Employee lookups are NOT ported — FABSMAIN exists on
///      this server but the app's login has no GRANT to it ("The server
///      principal ... is not able to access the database 'FABSMAIN'"), and
///      PAYROLL doesn't exist on this instance at all.
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
        Dictionary<string, string?> GroupByItem,
        Dictionary<string, string?> DivByGroup,
        Dictionary<string, string?> GroupNameByGroup);

    private async Task<EnrichmentLookups> EnrichAsync(IReadOnlyList<RawPairRow> raw, CancellationToken ct)
    {
        var groupByItem    = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var divByGroup     = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var groupNameByGroup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var itemCodes = raw.Select(r => r.ItemCode).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray();
        if (itemCodes.Length == 0)
            return new EnrichmentLookups(groupByItem, divByGroup, groupNameByGroup);

        await using var c = OpenOnPremBackup();

        // ItemCode IN @codes as a Dapper array expands into one SQL parameter per
        // item — a busy multi-day Robo pairing window easily exceeds SQL Server's
        // hard 2100-parameter limit ("The incoming request has too many parameters").
        // Same fix as the Counting Completion Today-mode UPC enrichment: pass the
        // list as a single CSV string, split it into an indexed temp table server-
        // side, then join.
        var itemCodesCsv = string.Join(",", itemCodes);
        var groupRows = await c.QueryAsync<(string Itemcode, string? GroupCode)>(new CommandDefinition(@"
            SELECT CAST(value AS VARCHAR(50)) AS ItemCode INTO #jrItems FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE UNIQUE CLUSTERED INDEX IX_jrItems_tmp ON #jrItems(ItemCode);

            SELECT b.Itemcode, GroupCode = MAX(b.GroupCode)
              FROM USA.dbo.UPCBarCodes b
              INNER JOIN #jrItems i ON i.ItemCode = b.Itemcode
             GROUP BY b.Itemcode;",
            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        foreach (var r in groupRows) groupByItem[r.Itemcode] = r.GroupCode;

        var groupCodes = groupByItem.Values.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToArray()!;
        if (groupCodes.Length > 0)
        {
            // GroupCode counts are bounded by distinct ItemCodes and are typically far
            // smaller, but use the same CSV pattern for safety rather than assuming a
            // ceiling.
            var groupCodesCsv = string.Join(",", groupCodes);
            var divRows = await c.QueryAsync<(string GroupCode, string? DivisionY)>(new CommandDefinition(@"
                SELECT CAST(value AS VARCHAR(50)) AS GroupCode INTO #jrGroups FROM STRING_SPLIT(@groupCodesCsv, ',');
                CREATE UNIQUE CLUSTERED INDEX IX_jrGroups_tmp ON #jrGroups(GroupCode);

                SELECT DISTINCT p.GroupCode, p.DivisionY
                  FROM USA.dbo.USAPriority p
                  INNER JOIN #jrGroups g ON g.GroupCode = p.GroupCode;",
                new { groupCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in divRows) divByGroup[r.GroupCode] = r.DivisionY;

            var nameRows = await c.QueryAsync<(string GroupCode, string? Description)>(new CommandDefinition(@"
                SELECT CAST(value AS VARCHAR(50)) AS GroupCode INTO #jrGroupNames FROM STRING_SPLIT(@groupCodesCsv, ',');
                CREATE UNIQUE CLUSTERED INDEX IX_jrGroupNames_tmp ON #jrGroupNames(GroupCode);

                SELECT ig.GroupCode, ig.Description
                  FROM hodata.dbo.itemgroup ig
                  INNER JOIN #jrGroupNames g ON g.GroupCode = ig.GroupCode;",
                new { groupCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in nameRows) groupNameByGroup[r.GroupCode] = r.Description;
        }

        return new EnrichmentLookups(groupByItem, divByGroup, groupNameByGroup);
    }

    /// <summary>
    /// Total Qty only, for the summary-card total. Just sums GetSummaryAsync's rows —
    /// the 7-day range cap already keeps this cheap, so it's not worth a separate
    /// SUM-only query path like the Manual/Export services have. Optionally restricted
    /// to a set of Divisions (from the report's Divisions filter).
    /// </summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, string? username, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var rows = await GetSummaryAsync(fromDate, toDate, username, ct);
        return rows
            .Where(r => divisions is not { Count: > 0 } || divisions.Contains(r.Division, StringComparer.OrdinalIgnoreCase))
            .Sum(r => r.Qty);
    }

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
                Username = r.EmpCode,
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
                    Username  = r.EmpCode,
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
