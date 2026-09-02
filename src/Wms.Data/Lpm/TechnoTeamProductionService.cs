using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record TechnoTeamProductionRow(
    DateTime TrnDate,
    int PairingAutoAMShift, int PairingAutoPMShift, int PairingManualAMShift,
    int ProductionManualAMShift, int ProductionManualPMShift,
    int ProductionAutoAMShift, int ProductionAutoPMShift);

/// <summary>
/// TECHNO whole-team production/pairing counts — one row PER DAY (not per
/// employee), split into AM/PM shift buckets at a configurable boundary hour.
/// Adapted from the legacy "TECHNO WH Production Report" desktop app.
///
/// Four sources, all on OnPremBackupDB (BFLDATA):
///   PairingAuto   -> BFLDATA.dbo.RFPairingCount, Type='RP' (Ch0..Ch21, no Ch22)
///   PairingManual -> BFLDATA.dbo.RFPairingCount, Type='PR' (same columns,
///                     summed WHOLE day — the legacy report never splits
///                     pairing-manual into AM/PM, hence one column only)
///   ProductionManual -> BFLDATA.dbo.DailyCountCategoryTrf, Warehouse='TECHNO' (HR0A..HR22A)
///   ProductionAuto    -> BFLDATA.dbo.DailyCountCategoryTrfRobo, Warehouse='TECHNO' (HR0A..HR22A)
///
/// The AM/PM split point depends on the selected "morning shift" option —
/// the legacy app hardcoded four near-identical SQL blocks (one per option);
/// this instead derives the AM hour count once and builds the column-sum
/// expressions from it, so there's a single source of truth instead of four
/// copies to keep in sync (a duplicated-block pattern that caused a real
/// off-by-one bug elsewhere in this codebase — see TechnoBuildingService).
///
/// ProductionManual is reported net of ProductionAuto for the same shift
/// (ProductionManual - ProductionAuto, floor at whatever CEILING gives) —
/// this matches the legacy report exactly: DailyCountCategoryTrf's "manual"
/// figures apparently include auto-handled pieces too, so the auto count is
/// subtracted out to get the manual-only figure. Verified against the
/// legacy desktop app's live output for 2026-09-01 / 6AM-8PM / Multiplier
/// checked — all seven columns matched exactly.
///
/// The "Multiplier?" toggle applies ISNULL(Trf_Multiplier,1) to Production
/// (not Pairing, which has no such column) before summing — CEILING then
/// rounds the weighted total up to a whole number, matching the legacy
/// report's use of CEILING to avoid fractional incentive counts.
///
/// A recursive date spine fills in every date in the range (even those with
/// zero rows in all four sources) so the grid always shows one row per day,
/// same as the legacy report. Capped at 366 days like the original.
/// </summary>
public class TechnoTeamProductionService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    public static readonly string[] ShiftTimings = ["7AM-4PM", "8AM-5PM", "6AM-6PM", "6AM-8PM"];

    private static readonly Dictionary<string, int> ShiftAmHourCount = new()
    {
        ["7AM-4PM"] = 10,
        ["8AM-5PM"] = 11,
        ["6AM-6PM"] = 12,
        ["6AM-8PM"] = 14,
    };

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

    /// <summary>Builds "ISNULL(col0,0)+ISNULL(col1,0)+..." for the inclusive
    /// index range, e.g. SumExpr("Ch{0}", 0, 13, false) or
    /// SumExpr("hr{0}a", 14, 22, true) (which wraps the sum with the
    /// per-row Trf_Multiplier before SUM, matching the legacy report's
    /// distributive-law-equivalent rewrite of its per-column *mult terms).</summary>
    private static string SumExpr(string colPattern, int fromIdx, int toIdxInclusive, bool withMultiplier)
    {
        var terms = Enumerable.Range(fromIdx, toIdxInclusive - fromIdx + 1)
            .Select(i => $"ISNULL({string.Format(colPattern, i)},0)");
        var sum = string.Join("+", terms);
        return withMultiplier ? $"SUM(({sum})*ISNULL(Trf_Multiplier,1))" : $"SUM({sum})";
    }

    public async Task<List<TechnoTeamProductionRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, string shiftTiming, bool useMultiplier, CancellationToken ct = default)
    {
        if (!ShiftAmHourCount.TryGetValue(shiftTiming, out var amHours))
            throw new ArgumentException($"Unknown shift timing '{shiftTiming}'.", nameof(shiftTiming));

        var pairingAutoAm = SumExpr("Ch{0}", 0, amHours - 1, false);
        var pairingAutoPm = SumExpr("Ch{0}", amHours, 21, false);
        var pairingManualAll = SumExpr("Ch{0}", 0, 21, false);
        var prodManualAm = SumExpr("hr{0}a", 0, amHours - 1, useMultiplier);
        var prodManualPm = SumExpr("hr{0}a", amHours, 22, useMultiplier);
        var prodAutoAm = SumExpr("hr{0}a", 0, amHours - 1, useMultiplier);
        var prodAutoPm = SumExpr("hr{0}a", amHours, 22, useMultiplier);

        var sql = $@"
            ;WITH DateSpine AS (
                SELECT CAST(@fromDate AS DATE) AS TrnDate
                UNION ALL
                SELECT DATEADD(day, 1, TrnDate) FROM DateSpine WHERE TrnDate < CAST(@toDate AS DATE)
            ),
            PairingAuto AS (
                SELECT TrnDate, AutoAM = {pairingAutoAm}, AutoPM = {pairingAutoPm}
                  FROM BFLDATA.dbo.RFPairingCount
                 WHERE Type = 'RP' AND TrnDate >= @fromDate AND TrnDate <= @toDate
                 GROUP BY TrnDate
            ),
            PairingManual AS (
                SELECT TrnDate, ManualAM = {pairingManualAll}
                  FROM BFLDATA.dbo.RFPairingCount
                 WHERE Type = 'PR' AND TrnDate >= @fromDate AND TrnDate <= @toDate
                 GROUP BY TrnDate
            ),
            ProdManual AS (
                SELECT TrnDate, ManualAM = {prodManualAm}, ManualPM = {prodManualPm}
                  FROM BFLDATA.dbo.DailyCountCategoryTrf
                 WHERE Warehouse = 'TECHNO' AND TrnDate >= @fromDate AND TrnDate <= @toDate
                 GROUP BY TrnDate
            ),
            ProdAuto AS (
                SELECT TrnDate, AutoAM = {prodAutoAm}, AutoPM = {prodAutoPm}
                  FROM BFLDATA.dbo.DailyCountCategoryTrfRobo
                 WHERE Warehouse = 'TECHNO' AND TrnDate >= @fromDate AND TrnDate <= @toDate
                 GROUP BY TrnDate
            )
            SELECT TrnDate = ds.TrnDate,
                   PairingAutoAMShift      = ISNULL(pa.AutoAM, 0),
                   PairingAutoPMShift      = ISNULL(pa.AutoPM, 0),
                   PairingManualAMShift    = ISNULL(pm.ManualAM, 0),
                   ProductionManualAMShift = CAST(CEILING(ISNULL(prm.ManualAM,0) - ISNULL(pra.AutoAM,0)) AS INT),
                   ProductionManualPMShift = CAST(CEILING(ISNULL(prm.ManualPM,0) - ISNULL(pra.AutoPM,0)) AS INT),
                   ProductionAutoAMShift   = CAST(CEILING(ISNULL(pra.AutoAM,0)) AS INT),
                   ProductionAutoPMShift   = CAST(CEILING(ISNULL(pra.AutoPM,0)) AS INT)
              FROM DateSpine ds
              LEFT JOIN PairingAuto pa   ON pa.TrnDate = ds.TrnDate
              LEFT JOIN PairingManual pm ON pm.TrnDate = ds.TrnDate
              LEFT JOIN ProdManual prm   ON prm.TrnDate = ds.TrnDate
              LEFT JOIN ProdAuto pra     ON pra.TrnDate = ds.TrnDate
             ORDER BY ds.TrnDate
             OPTION (MAXRECURSION 366);";

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<TechnoTeamProductionRow>(new CommandDefinition(
            sql, new { fromDate, toDate }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
