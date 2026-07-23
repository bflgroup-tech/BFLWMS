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
///      ('ROBO%') keep it as-is; otherwise resolve via FABSMAIN..[user]
///      (UserName → RecStartingNo, used as a fallback empcode) and then
///      PAYROLL..Employee (empcode → EmpName) — both FABSMAIN and PAYROLL
///      only exist on this robotics server, not on OnPremBackupDB.
///   4) GroupCode: hodata..itemmaster first, USA..UPCbarcodes as a fallback
///      when itemmaster has no match.
///   5) Division: USA..USAPRIORITY.DivisionY via GroupCode — deduplicated
///      into its own temp table first (unlike the legacy scalar subquery)
///      since USAPRIORITY can have more than one row per GroupCode, which
///      would otherwise raise "Subquery returned more than 1 value".
///   6) GroupName (Detailed only): hodata..itemgroup.description via GroupCode.
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

        CREATE TABLE #pair (area varchar(10), trndate smalldatetime, trntime varchar(15),
                            itemcode varchar(20), empcode varchar(20), qty int);

        INSERT INTO #pair (area, trndate, trntime, itemcode, empcode, qty)
        SELECT 'AUTO', TrnDate, TrnTime, itemcode, username, COUNT(*)
          FROM ROBOTICS.dbo.PairingConformationDetail
         WHERE TrnDate BETWEEN @from AND @to
           AND (@username IS NULL OR username = @username)
         GROUP BY TrnDate, TrnTime, username, itemcode;

        ALTER TABLE #pair ADD grp varchar(5), empname varchar(150), div varchar(200);

        UPDATE #pair SET empname = empcode WHERE empcode LIKE 'ROBO%';

        UPDATE #pair SET trndate = DATEADD(day, -1, trndate)
         WHERE LEFT(trntime, 2) IN ('00','01','02','03','04');

        UPDATE a SET a.empcode = b.RecStartingNo
          FROM #pair a JOIN FABSMAIN..[user] b ON a.empcode = b.UserName
         WHERE ISNULL(a.empname, '') = '' AND ISNULL(b.RecStartingNo, '') <> '';

        UPDATE a SET a.empname = b.EmpName
          FROM #pair a JOIN PAYROLL..Employee b ON a.empcode = b.empcode
         WHERE ISNULL(a.empname, '') = '';

        UPDATE a SET a.grp = b.GroupCode
          FROM #pair a JOIN hodata..itemmaster b ON a.itemcode = b.ItemCode;

        UPDATE a SET a.grp = b.GroupCode
          FROM #pair a JOIN USA..UPCbarcodes b ON a.itemcode = b.ItemCode
         WHERE ISNULL(a.grp, '') = '';

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
                GroupName  = (SELECT ig.description FROM hodata..itemgroup ig WHERE ig.GroupCode = p.grp),
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
