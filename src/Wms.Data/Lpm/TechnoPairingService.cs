using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record TechnoPairingRow(
    string Type, string Area, DateTime TrnDate, string EmpCode, string? GroupName, string? Division, int Qty);

/// <summary>
/// TECHNO pairing-incentive counts — one row per (Area, TrnDate, EmpCode,
/// GroupName, Division), Qty = how many items that employee paired. Adapted
/// from an ad-hoc VB.NET report query.
///
/// Two independent sources, two different servers:
///   MANUAL -> BFLDATA.dbo.RFPairDetail via OnPremBackupDB_ConnectionString
///             (same on-prem instance as LPMSIM/DATAREPORTING/hodata/usa).
///             Excludes Station LIKE 'ST-%' and any TrfNo whose first
///             character is an active export country code.
///   AUTO   -> BFLDATA.dbo.RFPairDetail AND robotics.dbo.PairDetail on the
///             TECHNO warehouse's own robotics SQL Server, reached via
///             resolver.GetRoboticsConnectionString("TechnoRoboDb") — the
///             original ad-hoc query reached this server directly by IP via
///             OPENDATASOURCE, which doesn't work here (Ad Hoc Distributed
///             Queries is disabled on the OnPremBackup instance, and there's
///             no linked server to it either). TechnoRoboDb already reaches
///             this same server for chute mapping, confirmed with the user
///             to be the same physical box the old query pointed at.
///
/// A pairing scan with TrnTime hour 00-04 is credited to the PREVIOUS
/// calendar day (same "late shift" convention as WarehouseIncentivesService),
/// applied consistently in both the same-day and shifted branches — the
/// original ad-hoc query had an asymmetric boundary (hour 04 landed in BOTH
/// branches, double-counting it), fixed here at the user's request.
///
/// GroupName/Division come from hodata.dbo.itemmaster -> hodata.dbo.itemgroup
/// -> usa.dbo.usapriority (all three keyed uniquely, verified no fan-out risk
/// unlike DATAREPORTING.dbo.vUPC_SUBCLASS elsewhere in this codebase) — all
/// on OnPremBackup, so the AUTO/MANUAL raw rows are combined in C# and bulk-
/// copied into a temp table there for one enrichment + aggregation pass.
///
/// EmpName (FABSMAIN.dbo.[user] in the original query) is NOT included —
/// FABSMAIN isn't reachable from this app's login, same limitation already
/// accepted in JafzaDivisionProductionService/JafzaRoboProductionService and
/// in WarehouseIncentivesService's PAYROLL gap. EmpCode is shown as-is.
/// </summary>
public class TechnoPairingService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    // RFPairDetail is comparable in size to AMEChecking (a week is 300K+ raw
    // rows before aggregation) — same generous budget as WarehouseIncentivesService.
    private const int CommandTimeoutSeconds = 300;

    private record RawRow(string Area, DateTime TrnDate, string EmpCode, string Itemcode, int Qty);

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

    private SqlConnection OpenTechnoRobo()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetRoboticsConnectionString("TechnoRoboDb")));
        c.Open();
        return c;
    }

    /// <summary>Division list for the filter — every distinct DivisionY in
    /// usa.dbo.usapriority, the same table this report joins to.</summary>
    public async Task<List<string>> GetDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT DivisionY FROM usa.dbo.usapriority
             WHERE DivisionY IS NOT NULL AND DivisionY <> ''
             ORDER BY DivisionY",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private const string ManualSql = @"
        SELECT 'MANUAL' AS Area, EntryDate AS TrnDate, EmpCode, Itemcode, COUNT(*) AS Qty
          FROM BFLDATA.dbo.RFPairDetail
         WHERE EntryDate >= @fromDate AND EntryDate <= @toDate
           AND Station NOT LIKE 'ST-%'
           AND SUBSTRING(TrfNo, 1, 1) NOT IN (
               SELECT DISTINCT ExportCountryCode FROM BFLDATA.dbo.DataSettings
                WHERE ExportActive = 'Y' AND ExportCountryCode <> '')
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'MANUAL', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM BFLDATA.dbo.RFPairDetail
         WHERE EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND Station NOT LIKE 'ST-%'
           AND SUBSTRING(TrfNo, 1, 1) NOT IN (
               SELECT DISTINCT ExportCountryCode FROM BFLDATA.dbo.DataSettings
                WHERE ExportActive = 'Y' AND ExportCountryCode <> '')
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode;";

    private async Task<List<RawRow>> FetchManualAsync(DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<RawRow>(new CommandDefinition(
            ManualSql, new { fromDate, toDate },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // Both source tables (BFLDATA.dbo.RFPairDetail and robotics.dbo.PairDetail) live
    // on the SAME TechnoRoboDb server, so this is one connection, no linked server.
    private const string AutoSql = @"
        SELECT 'AUTO' AS Area, EntryDate AS TrnDate, EmpCode, Itemcode, COUNT(*) AS Qty
          FROM BFLDATA.dbo.RFPairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate >= @fromDate AND EntryDate <= @toDate
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', EntryDate, EmpCode, Itemcode, COUNT(*)
          FROM robotics.dbo.PairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate >= @fromDate AND EntryDate <= @toDate
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM BFLDATA.dbo.RFPairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM robotics.dbo.PairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode;";

    private async Task<List<RawRow>> FetchAutoAsync(DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        await using var c = OpenTechnoRobo();
        var rows = await c.QueryAsync<RawRow>(new CommandDefinition(
            AutoSql, new { fromDate, toDate },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<TechnoPairingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, IEnumerable<string>? areas, IEnumerable<string>? divisions,
        string? empCodeSearch, CancellationToken ct = default)
    {
        var manual = await FetchManualAsync(fromDate, toDate, ct);
        var auto = await FetchAutoAsync(fromDate, toDate, ct);
        var raw = manual.Concat(auto).ToList();
        if (raw.Count == 0) return new();

        var areaList = areas?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noAreaFilter = areas is null;
        var divisionList = divisions?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noDivisionFilter = divisions is null;
        var empCodeFilter = string.IsNullOrWhiteSpace(empCodeSearch) ? null : empCodeSearch.Trim();

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                CREATE TABLE #PairRaw (
                    Area     VARCHAR(10)  NOT NULL,
                    TrnDate  DATETIME     NOT NULL,
                    EmpCode  VARCHAR(20)  NOT NULL,
                    Itemcode VARCHAR(20)  NOT NULL,
                    Qty      INT          NOT NULL
                );",
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "#PairRaw",
                BulkCopyTimeout = CommandTimeoutSeconds,
            })
            {
                var table = new System.Data.DataTable();
                table.Columns.Add("Area", typeof(string));
                table.Columns.Add("TrnDate", typeof(DateTime));
                table.Columns.Add("EmpCode", typeof(string));
                table.Columns.Add("Itemcode", typeof(string));
                table.Columns.Add("Qty", typeof(int));
                foreach (var r in raw) table.Rows.Add(r.Area, r.TrnDate, r.EmpCode, r.Itemcode, r.Qty);
                await bulk.WriteToServerAsync(table, ct);
            }

            var rows = await c.QueryAsync<TechnoPairingRow>(new CommandDefinition(@"
                SELECT Type = 'PAIRING', p.Area, p.TrnDate, p.EmpCode,
                       GroupName = ig.Description,
                       Division = up.DivisionY,
                       Qty = SUM(p.Qty)
                  FROM #PairRaw p
                  LEFT JOIN hodata.dbo.itemmaster im ON im.ItemCode = p.Itemcode
                  LEFT JOIN hodata.dbo.itemgroup ig ON ig.GroupCode = im.GroupCode
                  LEFT JOIN usa.dbo.usapriority up ON up.groupCode = im.GroupCode
                 WHERE (@noAreaFilter = 1 OR p.Area IN @areas)
                   AND (@noDivisionFilter = 1 OR up.DivisionY IN @divisions)
                   AND (@empCodeFilter IS NULL OR p.EmpCode = @empCodeFilter)
                 GROUP BY p.Area, p.TrnDate, p.EmpCode, ig.Description, up.DivisionY
                 ORDER BY p.TrnDate, p.EmpCode;",
                new
                {
                    areas = areaList, noAreaFilter = noAreaFilter ? 1 : 0,
                    divisions = divisionList, noDivisionFilter = noDivisionFilter ? 1 : 0,
                    empCodeFilter,
                },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return rows.AsList();
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}
