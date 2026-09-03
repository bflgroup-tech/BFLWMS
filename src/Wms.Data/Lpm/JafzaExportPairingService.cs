using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record JafzaExportPairingRow(DateTime EntryDate, string Users, int TotalPairing, int R1SortingCnt);

/// <summary>
/// JAFZA export pairing + R1 sorting counts per day, credited either to a
/// single EmpCode or to a paired "EmpCode1-EmpCode2" label when two employees
/// are assigned as a pair for that day. Adapted from an ad-hoc VB.NET report.
///
/// Raw counts come from JafazaRoboDb (BFLDATA.dbo.rfPairDetail for manual
/// pairing, ROBOTICS.dbo.PairDetail split by BFLDATA.dbo.DataSettings.Dataname
/// into pairing-shop vs R1-sorting-shop buckets) — deliberately NOT filtered
/// by Station/warehouse the way TechnoPairingService's MANUAL fetch is,
/// because this is JafazaRoboDb's own local BFLDATA/ROBOTICS copy, scoped to
/// JAFZA by which physical server it lives on (confirmed distinct from the
/// central OnPremBackup mirror, which holds mixed-warehouse Station prefixes
/// for the identically-named rfPairDetail table).
///
/// The pair-assignment lookup (BFLDATA.dbo.PairAssign) is fetched from
/// OnPremBackup instead: the original ad-hoc query reached it via
/// OPENDATASOURCE to 192.168.5.51, which isn't available here (Ad Hoc
/// Distributed Queries disabled, same limitation hit for TechnoPairingService)
/// — PairAssign turned out to already be directly reachable on OnPremBackup's
/// BFLDATA, fresh through the current date, so no workaround was needed.
///
/// A matched pair's Emp_Pair label is always canonicalized to
/// "EmpCode1-EmpCode2" (the original query built the same string regardless
/// of which side of the pair EmpCode matched); an employee with no pair
/// assignment for that day is credited individually under their own EmpCode.
/// Verified live that no EmpCode has more than one pair assignment on the
/// same day in the current data.
/// </summary>
public class JafzaExportPairingService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private record RawCountRow(DateTime EntryDate, string EmpCode, int RfPaircnt, int PairCnt, int BuildingCnt);
    private record PairAssignRow(DateTime TrnDate, string EmpCode1, string EmpCode2);

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenJafazaRobo()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetRoboticsConnectionString("JafazaRoboDb")));
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    private const string RawCountSql = @"
        ;WITH RawCounts AS (
            SELECT EntryDate, EmpCode, RfPaircnt = COUNT(Itemcode), PairCnt = 0, BuildingCnt = 0
              FROM BFLDATA.dbo.rfPairDetail
             WHERE EntryDate >= @fromDate AND EntryDate <= @toDate
             GROUP BY EmpCode, EntryDate
            UNION ALL
            SELECT EntryDate, EmpCode, RfPaircnt = 0, PairCnt = COUNT(Itemcode), BuildingCnt = 0
              FROM ROBOTICS.dbo.PairDetail
             WHERE EntryDate >= @fromDate AND EntryDate <= @toDate
               AND ShopName IN (SELECT ShopName FROM BFLDATA.dbo.DataSettings WHERE Dataname <> '')
             GROUP BY EmpCode, EntryDate
            UNION ALL
            SELECT EntryDate, EmpCode, RfPaircnt = 0, PairCnt = 0, BuildingCnt = COUNT(Itemcode)
              FROM ROBOTICS.dbo.PairDetail
             WHERE EntryDate >= @fromDate AND EntryDate <= @toDate
               AND ShopName IN (SELECT ShopName FROM BFLDATA.dbo.DataSettings WHERE Dataname = '')
             GROUP BY EntryDate, EmpCode
        )
        SELECT EntryDate, EmpCode,
               RfPaircnt = SUM(RfPaircnt), PairCnt = SUM(PairCnt), BuildingCnt = SUM(BuildingCnt)
          FROM RawCounts
         GROUP BY EntryDate, EmpCode;";

    private const string PairAssignSql = @"
        SELECT TrnDate, EmpCode1, EmpCode2
          FROM BFLDATA.dbo.PairAssign
         WHERE TrnDate >= @fromDate AND TrnDate <= @toDate;";

    public async Task<List<JafzaExportPairingRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, string? userSearch, CancellationToken ct = default)
    {
        List<RawCountRow> raw;
        await using (var c = OpenJafazaRobo())
        {
            raw = (await c.QueryAsync<RawCountRow>(new CommandDefinition(
                RawCountSql, new { fromDate, toDate },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }
        if (raw.Count == 0) return new();

        List<PairAssignRow> pairs;
        await using (var c = OpenOnPremBackup())
        {
            pairs = (await c.QueryAsync<PairAssignRow>(new CommandDefinition(
                PairAssignSql, new { fromDate, toDate },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        var pairLookup = new Dictionary<(string EmpCode, DateTime Date), string>();
        foreach (var p in pairs)
        {
            var label = $"{p.EmpCode1}-{p.EmpCode2}";
            pairLookup[(p.EmpCode1, p.TrnDate)] = label;
            pairLookup[(p.EmpCode2, p.TrnDate)] = label;
        }

        var grouped = raw
            .Select(r => new
            {
                r.EntryDate,
                Users = pairLookup.GetValueOrDefault((r.EmpCode, r.EntryDate), r.EmpCode),
                TotalPairing = r.RfPaircnt + r.PairCnt,
                r.BuildingCnt,
            })
            .GroupBy(x => (x.EntryDate, x.Users))
            .Select(g => new JafzaExportPairingRow(
                g.Key.EntryDate, g.Key.Users, g.Sum(x => x.TotalPairing), g.Sum(x => x.BuildingCnt)))
            .OrderBy(r => r.EntryDate).ThenBy(r => r.Users);

        if (!string.IsNullOrWhiteSpace(userSearch))
        {
            var s = userSearch.Trim();
            grouped = grouped.Where(r => r.Users.Contains(s, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.EntryDate).ThenBy(r => r.Users);
        }

        return grouped.ToList();
    }
}
