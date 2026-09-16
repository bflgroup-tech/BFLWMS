using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Export Production Report. Ported directly from a user-supplied
/// query against BFLDATA.dbo.DailyCountCategoryTrf (Warehouse = 'JAFZA').
/// No item-level detail is available from this source, unlike Manual/Robo
/// — Summary only.
///
/// This table has no per-row timestamp — activity is stored in per-hour
/// bucket COLUMNS (HR0A, hr1a..hr22a) on each (TrnDate, Division, ShopName)
/// row. Same 3am shift-boundary rule as the other 3 sources: hours 00/01/02
/// belong to the PREVIOUS day's shift. Since those hours live on the day's
/// own row rather than a separate scannable row, the shift is done via a
/// self-join against the FOLLOWING day's row (same Division/ShopName):
/// a day's effective Qty = its own hr3a..hr22a ("late" hours) PLUS the
/// following day's HR0A+hr1a+hr2a ("early" hours, which shift back onto
/// this day) — and symmetrically, this day's own early hours flow onto
/// the PRECEDING day's total instead of this one's.
/// </summary>
public class JafzaExportProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private const string EarlyHourSum = "HR0A + hr1a + hr2a";
    private const string LateHourSum =
        "hr3a + hr4a + hr5a + hr6a + hr7a + hr8a + hr9a + hr10a + hr11a + hr12a + hr13a + " +
        "hr14a + hr15a + hr16a + hr17a + hr18a + hr19a + hr20a + hr21a + hr22a";

    // Bucketed per (TrnDate, Division, ShopName) one day past @to, so the
    // day-@to row can pick up @to+1's early hours via the self-join below.
    private const string BucketedCte = $@"
        WITH Bucketed AS (
            SELECT TrnDate, Division, ShopName,
                   EarlyQty = SUM({EarlyHourSum}),
                   LateQty  = SUM({LateHourSum})
              FROM BFLDATA.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE Warehouse = 'JAFZA' AND TrnDate >= @from AND TrnDate <= DATEADD(day, 1, @to)
             GROUP BY TrnDate, Division, ShopName
        )";

    /// <summary>Division-wise summary — one row per (TrnDate, Division, ShopName), shift-adjusted.</summary>
    public async Task<List<JafzaExportProductionRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // FULL OUTER JOIN, not LEFT: a (Division, ShopName) can have zero late-hour
        // activity on day X (no row in b) while still having early-hour activity on
        // day X+1 (a row only in n). A LEFT JOIN driven by b silently drops that
        // shop's early-hour Qty entirely, since there's no b row to attach it to.
        var rows = await c.QueryAsync<JafzaExportProductionRow>(new CommandDefinition(BucketedCte + @"
            SELECT TrnDate  = COALESCE(b.TrnDate, DATEADD(day, -1, n.TrnDate)),
                   Division = COALESCE(b.Division, n.Division),
                   ShopName = COALESCE(b.ShopName, n.ShopName),
                   Qty      = ISNULL(b.LateQty, 0) + ISNULL(n.EarlyQty, 0)
              FROM Bucketed b
              FULL OUTER JOIN Bucketed n
                ON n.TrnDate = DATEADD(day, 1, b.TrnDate) AND n.Division = b.Division AND n.ShopName = b.ShopName
             WHERE COALESCE(b.TrnDate, DATEADD(day, -1, n.TrnDate)) >= @from
               AND COALESCE(b.TrnDate, DATEADD(day, -1, n.TrnDate)) <= @to
             ORDER BY TrnDate, Division, ShopName",
            new { from = fromDate.Date, to = toDate.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Total Qty only, for the summary-card total.
    /// Optionally restricted to a set of Divisions (from the report's Divisions filter).</summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var rows = await GetSummaryAsync(fromDate, toDate, ct);
        return rows
            .Where(r => divisions is not { Count: > 0 } || divisions.Contains(r.Division, StringComparer.OrdinalIgnoreCase))
            .Sum(r => r.Qty);
    }
}
