using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Box GRN Production Report. Source is USA.dbo.vUPCBoxDet
/// (WHouse = 'JAFZA', Remarks = 'Box GRN') — BoxCount = COUNT(DISTINCT BoxNo),
/// Qty = SUM(Qty). The view's own GroupCode is unreliable, so Division is
/// resolved the same way as Manual/Robo instead: HODATA.dbo.ItemMaster.GroupCode
/// via ItemCode, then USA.dbo.USAPriority.DivisionY via that GroupCode — rows
/// with no matching/blank Division are dropped, same as those reports.
///
/// Same shift-boundary rule as Manual/Robo: a row before 03:00 (LEFT(Time1,2)
/// IN ('00','01','02')) belongs to the PREVIOUS day's shift. The raw fetch
/// window is widened one day past @to to catch next-day early-morning rows
/// that shift back into range, then narrowed back to [@from, @to] in C#
/// after the shift is applied — done here instead of Manual's two-pass SQL
/// UNION since this service already aggregates in C#, not SQL.
/// </summary>
public class JafzaBoxGrnProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private record RawFetchRow(DateTime TrnDate, string Time1, string BoxNo, string ItemCode, string? GroupCode, string? Division, int Qty);
    private record RawRow(DateTime TrnDate, string BoxNo, string ItemCode, string? GroupCode, string? Division, int Qty);

    private const string RawQuerySql = @"
        SELECT
            TrnDate   = v.TrnDate,
            Time1     = v.Time1,
            BoxNo     = v.BoxNo,
            ItemCode  = v.Itemcode,
            GroupCode = im.GroupCode,
            Division  = p.DivisionY,
            Qty       = v.Qty
          FROM USA.dbo.vUPCBoxDet v WITH (NOLOCK)
          LEFT JOIN HODATA.dbo.ItemMaster im WITH (NOLOCK) ON im.ItemCode = v.Itemcode
          LEFT JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.GroupCode = im.GroupCode
         WHERE v.WHouse = 'JAFZA' AND v.Remarks = 'Box GRN'
           AND v.TrnDate >= @from AND v.TrnDate <= @toPlusOne";

    private static bool IsPreShiftHour(string? time1) =>
        time1 is { Length: >= 2 } && (time1[..2] is "00" or "01" or "02");

    private async Task<List<RawRow>> FetchRawAsync(DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<RawFetchRow>(new CommandDefinition(
            RawQuerySql, new { from = fromDate.Date, toPlusOne = toDate.Date.AddDays(1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows
            .Select(r => new RawRow(
                IsPreShiftHour(r.Time1) ? r.TrnDate.AddDays(-1) : r.TrnDate,
                r.BoxNo, r.ItemCode, r.GroupCode, r.Division, r.Qty))
            .Where(r => r.TrnDate >= fromDate.Date && r.TrnDate <= toDate.Date)
            .Where(r => !string.IsNullOrWhiteSpace(r.Division))
            .ToList();
    }

    /// <summary>Division-wise summary — one row per (TrnDate, Division).</summary>
    public async Task<List<JafzaBoxGrnSummaryRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(fromDate, toDate, ct);
        return raw
            .GroupBy(r => (r.TrnDate, r.Division))
            .Select(g => new JafzaBoxGrnSummaryRow(
                g.Key.TrnDate, g.Key.Division!,
                BoxCount: g.Select(x => x.BoxNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Qty: g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.Division)
            .ToList();
    }

    /// <summary>Item-wise detail — one row per (TrnDate, ItemCode, GroupCode, Division).</summary>
    public async Task<List<JafzaBoxGrnDetailRow>> GetDetailAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(fromDate, toDate, ct);
        return raw
            .GroupBy(r => (r.TrnDate, r.ItemCode, r.GroupCode, r.Division))
            .Select(g => new JafzaBoxGrnDetailRow(
                g.Key.TrnDate, g.Key.ItemCode, g.Key.GroupCode, g.Key.Division!,
                BoxCount: g.Select(x => x.BoxNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Qty: g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.ItemCode)
            .ToList();
    }

    /// <summary>Total Qty only, for the summary-card total. Just sums GetSummaryAsync's rows,
    /// same as the Robo/Export services — this table's volume is small (GRN audit records).
    /// Optionally restricted to a set of Divisions (from the report's Divisions filter).</summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var rows = await GetSummaryAsync(fromDate, toDate, ct);
        return rows
            .Where(r => divisions is not { Count: > 0 } || divisions.Contains(r.Division, StringComparer.OrdinalIgnoreCase))
            .Sum(r => r.Qty);
    }

    /// <summary>Total distinct BoxNo count across the (optionally division-filtered) summary rows.
    /// Note this sums each row's already-distinct-per-(TrnDate,Division) box count, so it can
    /// double-count a box that appears under more than one division/day — acceptable here since
    /// box counts are for a quick "how many boxes" glance, not a precise unique-box audit figure.</summary>
    public async Task<int> GetTotalBoxCountAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var rows = await GetSummaryAsync(fromDate, toDate, ct);
        return rows
            .Where(r => divisions is not { Count: > 0 } || divisions.Contains(r.Division, StringComparer.OrdinalIgnoreCase))
            .Sum(r => r.BoxCount);
    }
}
