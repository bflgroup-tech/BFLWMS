using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Robo Production Report. Ported from a legacy VB.NET desktop query
/// ("#pair") against ROBOTICS.dbo.PairingConformationDetail, reached via the
/// JafazaRoboDb connection (same one used by the Chute Mapping page) —
/// NOT the OnPremBackupDB connection the Manual (PhotoChecking-based)
/// production report uses.
///
/// Legacy logic, preserved as closely as possible:
///   1) #pair = one row per (TrnDate, TrnTime, username, itemcode), Qty = COUNT(*),
///      Area hardcoded to 'AUTO'.
///   2) Scans before 05:00 belong to the previous day (TrnDate -= 1 when
///      LEFT(TrnTime,2) IN ('00'..'04')) — same shift-boundary rule as the
///      Manual report.
///   3) Username → display name: if it already looks like a robot login
///      ('ROBO%') keep it as-is; otherwise fall back to the raw empcode.
///      The legacy FABSMAIN..[user] and PAYROLL..Employee name lookups are
///      NOT ported — neither table is reachable from the JafazaRoboDb
///      connection ("Invalid object name 'FABSMAIN..user'" confirmed;
///      PAYROLL removed proactively for the same reason — this is a
///      dedicated robotics server, unlikely to also host general HR data).
///   4) GroupCode: USA..UPCbarcodes via ItemCode. The legacy hodata..itemmaster
///      lookup (tried first, before the UPCbarcodes fallback) is NOT ported —
///      hodata isn't reachable from this connection either
///      ("Invalid object name 'hodata..itemmaster'").
///   5) Division: USA..USAPRIORITY.DivisionY via GroupCode — deduplicated
///      into its own temp table first (unlike the legacy scalar subquery)
///      since USAPRIORITY can have more than one row per GroupCode, which
///      would otherwise raise "Subquery returned more than 1 value".
///   6) GroupName (Detailed only): NOT populated (always null) — the legacy
///      source was hodata..itemgroup.description, same unreachable database.
/// </summary>
public class JafzaRoboProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;
    private const string ConnectionStringKey = "JafazaRoboDb";

    private SqlConnection OpenRobo()
    {
        var c = new SqlConnection(resolver.GetRoboticsConnectionString(ConnectionStringKey));
        c.Open();
        return c;
    }

    // Materialises #pair (enriched) — must be prepended to whatever query
    // reads it, so the build + read happen in the SAME Dapper command (same
    // lesson as the #JafzaBase prefix in JafzaDivisionProductionService).
    private const string PairPrefix = @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#pair')  IS NOT NULL DROP TABLE #pair;
        IF OBJECT_ID('tempdb..#div')   IS NOT NULL DROP TABLE #div;

        CREATE TABLE #pair (area varchar(20), trndate smalldatetime, trntime varchar(30),
                            itemcode varchar(50), empcode varchar(50), qty int);

        INSERT INTO #pair (area, trndate, trntime, itemcode, empcode, qty)
        SELECT 'AUTO', TrnDate, TrnTime, itemcode, username, COUNT(*)
          FROM ROBOTICS.dbo.PairingConformationDetail
         WHERE TrnDate BETWEEN @from AND @to
           AND (@username IS NULL OR username = @username)
         GROUP BY TrnDate, TrnTime, username, itemcode;

        ALTER TABLE #pair ADD grp varchar(10), empname varchar(200), div varchar(200);

        UPDATE #pair SET empname = empcode WHERE empcode LIKE 'ROBO%';

        UPDATE #pair SET trndate = DATEADD(day, -1, trndate)
         WHERE LEFT(trntime, 2) IN ('00','01','02','03','04');

        UPDATE a SET a.grp = b.GroupCode
          FROM #pair a JOIN USA..UPCbarcodes b ON a.itemcode = b.ItemCode;

        SELECT DISTINCT GroupCode, DivisionY
          INTO #div
          FROM USA..USAPRIORITY
         WHERE GroupCode IN (SELECT DISTINCT grp FROM #pair WHERE grp IS NOT NULL);

        CREATE CLUSTERED INDEX IX_div ON #div (GroupCode);

        UPDATE a SET a.div = d.DivisionY
          FROM #pair a JOIN #div d ON d.GroupCode = a.grp;
        ";

    /// <summary>Division-wise summary — one row per (TrnDate, Division, Username).</summary>
    public async Task<List<JafzaRoboProductionSummaryRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        await using var c = OpenRobo();
        var rows = await c.QueryAsync<JafzaRoboProductionSummaryRow>(new CommandDefinition(PairPrefix + @"
            SELECT
                p.trndate  AS TrnDate,
                Division   = p.div,
                Username   = ISNULL(NULLIF(p.empname, ''), p.empcode),
                Qty        = SUM(p.qty)
              FROM #pair p
             WHERE ISNULL(p.div, '') <> ''
             GROUP BY p.trndate, p.div, ISNULL(NULLIF(p.empname, ''), p.empcode)
             ORDER BY p.trndate, Division, Username;

            DROP TABLE #pair, #div;",
            new { from = fromDate.Date, to = toDate.Date, username = usernameFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Item-wise detail — one row per (TrnDate, ItemCode, Username, GroupCode).</summary>
    public async Task<List<JafzaRoboProductionDetailRow>> GetDetailAsync(
        DateTime fromDate, DateTime toDate, string? username, CancellationToken ct = default)
    {
        var usernameFilter = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        await using var c = OpenRobo();
        var rows = await c.QueryAsync<JafzaRoboProductionDetailRow>(new CommandDefinition(PairPrefix + @"
            SELECT
                p.trndate  AS TrnDate,
                p.itemcode AS ItemCode,
                Username   = ISNULL(NULLIF(p.empname, ''), p.empcode),
                GroupCode  = p.grp,
                GroupName  = CAST(NULL AS varchar(200)),
                Division   = p.div,
                Qty        = SUM(p.qty)
              FROM #pair p
             WHERE ISNULL(p.div, '') <> ''
             GROUP BY p.trndate, p.itemcode, ISNULL(NULLIF(p.empname, ''), p.empcode), p.grp, p.div
             ORDER BY p.trndate, p.itemcode;

            DROP TABLE #pair, #div;",
            new { from = fromDate.Date, to = toDate.Date, username = usernameFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
