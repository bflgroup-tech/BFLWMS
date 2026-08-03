using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Division-wise Production Report. Ported from a legacy VB.NET desktop
/// query against Online.dbo.PhotoChecking (Warehouse = 'JAFZA').
///
/// The legacy query ran in two passes because scans before 05:00 belong to
/// the PREVIOUS production day's shift:
///   1) TrnDate rows where LEFT(Time1,2) NOT IN ('00'..'04') — kept as-is.
///   2) TrnDate rows where LEFT(Time1,2) IN ('00'..'04') — TrnDate shifted
///      back one day.
/// Both passes are UNIONed here into one #JafzaBase temp table (same idea,
/// just without the legacy per-username dynamic table name — Dapper already
/// gives each request its own connection/session).
///
/// Division comes from USA.dbo.USAPriority.DivisionY via GroupCode; rows
/// with no matching/blank Division are dropped (matches the legacy
/// "delete where isnull(Division,'')=''" step). LPMDT/OraPONo are read
/// directly off PhotoChecking (confirmed present on the live schema) and
/// are part of the detail grain, since the same UPC can be scanned under
/// different POs/production runs on the same day. The legacy
/// PAYROLL.dbo.employee name lookup is intentionally NOT ported (that
/// database isn't reachable from this connection) — Username is the raw
/// CheckedBy value.
/// </summary>
public class JafzaDivisionProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>
    /// Business-week options for the Week filter — distinct (year, week) pairs from
    /// LPMSIM.dbo.BFL_MFP_OUTBOUND_T1, restricted to the window [1st of last calendar
    /// month .. end of the current Sunday-Saturday week]. That table has no per-week
    /// date at all — every row in it shares essentially the same batch-load createts
    /// (confirmed: a single load populates a multi-year rolling horizon, e.g. 2025 Wk52
    /// through 2027 Wk52 all stamped with the same createts) — so createts can't be
    /// used to tell weeks apart or to filter by "current week" at all. Dates are
    /// instead computed per (year, week) via JafzaWeekOption's fiscal-week formula;
    /// see its doc comment for the caveat that the formula is inferred, not looked up.
    /// GROUP BY must include year, not just week — the same week number recurs across
    /// fiscal years (e.g. both 2026 and 2027 have a Wk31) and collapsing them together
    /// would silently pick one year's week over the other.
    /// </summary>
    public async Task<List<JafzaWeekOption>> GetAvailableWeeksAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var startOfLastMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var startOfCurrentWeek = today.AddDays(-(int)today.DayOfWeek);
        var endOfCurrentWeek = startOfCurrentWeek.AddDays(6);

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(int Year, int Wk)>(new CommandDefinition(@"
            SELECT DISTINCT [year], week
              FROM LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 WITH (NOLOCK)",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows
            .Select(r => new JafzaWeekOption(r.Wk, FirstSundayOfJanuary(r.Year).AddDays((r.Wk - 1) * 7)))
            .Where(w => w.OtsDate >= startOfLastMonth && w.OtsDate <= endOfCurrentWeek)
            .OrderByDescending(w => w.OtsDate)
            .ToList();
    }

    // The Sunday immediately preceding the first Monday of January — i.e. the Sunday
    // that starts fiscal week 1. If Jan1 itself is a Monday, that's simply Dec31 of
    // the prior year.
    private static DateTime FirstSundayOfJanuary(int year)
    {
        var jan1 = new DateTime(year, 1, 1);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)jan1.DayOfWeek + 7) % 7;
        return jan1.AddDays(daysUntilMonday - 1);
    }

    // Materialises #JafzaBase / #JafzaDiv — must be prepended to whatever
    // query reads them, so the build + read happen in the SAME Dapper
    // command (= same SQL Server batch). Splitting it across separate
    // ExecuteAsync/QueryAsync calls drops the temp tables between commands
    // (confirmed in testing — "Invalid object name '#JafzaBase'").
    private const string BasePrefix = @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#JafzaBase') IS NOT NULL DROP TABLE #JafzaBase;
        IF OBJECT_ID('tempdb..#JafzaDiv')  IS NOT NULL DROP TABLE #JafzaDiv;

        SELECT TrnDate, Time1, UPC, CheckedQty = COUNT(UPC), CheckedBy, GroupCode, LPMDT = LPMdt, OraPONo
          INTO #JafzaBase
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate >= @from AND TrnDate <= @to
           AND LEFT(Time1, 2) NOT IN ('00','01','02','03','04')
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        INSERT INTO #JafzaBase (TrnDate, Time1, UPC, CheckedQty, CheckedBy, GroupCode, LPMDT, OraPONo)
        SELECT DATEADD(day, -1, TrnDate), Time1, UPC, COUNT(UPC), CheckedBy, GroupCode, LPMdt, OraPONo
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate > @from AND TrnDate <= DATEADD(day, 1, @to)
           AND LEFT(Time1, 2) IN ('00','01','02','03','04')
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        CREATE CLUSTERED INDEX IX_JafzaBase ON #JafzaBase (UPC);

        SELECT DISTINCT GroupCode, DivisionY
          INTO #JafzaDiv
          FROM USA.dbo.USAPriority WITH (NOLOCK)
         WHERE GroupCode IN (SELECT DISTINCT GroupCode FROM #JafzaBase);

        CREATE CLUSTERED INDEX IX_JafzaDiv ON #JafzaDiv (GroupCode);
        ";

    /// <summary>Division-wise summary — one row per (TrnDate, Division, Username).</summary>
    public async Task<List<JafzaProductionSummaryRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<JafzaProductionSummaryRow>(new CommandDefinition(BasePrefix + @"
            SELECT
                b.TrnDate,
                Division   = d.DivisionY,
                Username   = b.CheckedBy,
                CheckedQty = SUM(b.CheckedQty)
              FROM #JafzaBase b
              JOIN #JafzaDiv d ON d.GroupCode = b.GroupCode
             WHERE ISNULL(d.DivisionY, '') <> ''
             GROUP BY b.TrnDate, d.DivisionY, b.CheckedBy
             ORDER BY b.TrnDate, Division, Username;

            DROP TABLE #JafzaBase, #JafzaDiv;",
            new { from = fromDate.Date, to = toDate.Date, username = usernameFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Total CheckedQty only, for the summary-card total — avoids materializing every row.
    /// Optionally restricted to a set of Divisions (from the report's Divisions filter).</summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, string? username, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        var divisionFilterSql = divisions is { Count: > 0 } ? "AND d.DivisionY IN @divisions" : "";
        await using var c = OpenOnPremBackup();
        var total = await c.ExecuteScalarAsync<int?>(new CommandDefinition(BasePrefix + $@"
            SELECT SUM(b.CheckedQty)
              FROM #JafzaBase b
              JOIN #JafzaDiv d ON d.GroupCode = b.GroupCode
             WHERE ISNULL(d.DivisionY, '') <> '' {divisionFilterSql};

            DROP TABLE #JafzaBase, #JafzaDiv;",
            new { from = fromDate.Date, to = toDate.Date, username = usernameFilter, divisions },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return total ?? 0;
    }

    /// <summary>Item-wise detail — one row per (TrnDate, UPC, Username, GroupCode, LPMDT, OraPONo).</summary>
    public async Task<List<JafzaProductionDetailRow>> GetDetailAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<JafzaProductionDetailRow>(new CommandDefinition(BasePrefix + @"
            SELECT
                b.TrnDate,
                b.UPC,
                Username   = b.CheckedBy,
                b.GroupCode,
                Division   = d.DivisionY,
                CheckedQty = SUM(b.CheckedQty),
                Lpmdt      = b.LPMDT,
                OraPoNo    = b.OraPONo
              FROM #JafzaBase b
              JOIN #JafzaDiv d ON d.GroupCode = b.GroupCode
             WHERE ISNULL(d.DivisionY, '') <> ''
             GROUP BY b.TrnDate, b.UPC, b.CheckedBy, b.GroupCode, d.DivisionY, b.LPMDT, b.OraPONo
             ORDER BY b.TrnDate, b.UPC;

            DROP TABLE #JafzaBase, #JafzaDiv;",
            new { from = fromDate.Date, to = toDate.Date, username = usernameFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
