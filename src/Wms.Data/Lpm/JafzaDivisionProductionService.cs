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
