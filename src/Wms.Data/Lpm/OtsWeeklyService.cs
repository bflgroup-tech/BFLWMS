namespace Wms.Data.Lpm;

/// <summary>
/// Weekly OTS generate — driven by the Monday 05:00 GST scheduled service
/// (Wms.Web.Hosting.WeeklyOtsBatchService), one hour after the Volume Group
/// refresh so it reads a fresh StoreDivGrade snapshot.
///
/// OTS has no per-country dimension: GenerateAndPersistAsync computes every
/// country in one pass and stamps the rows with OTSDate = today (GST). So this
/// job uses the single-row activation convention (Country = "") rather than a
/// row per country.
///
/// PRECONDITION (enforced inside OtsPoAllocationService.GenerateAndPersistAsync):
/// dbo.StoreDivGrade must already hold rows generated TODAY. That table is only
/// written when Volume Group runs for BFLGROUP — so 'BFLGROUP' must be an active
/// country under JobName='VolumeGroupWeekly', otherwise this job fails every week
/// with "Volume Group has not been generated today". RunOnceAsync turns that into
/// an explicit up-front message instead of letting it surface as a raw throw.
/// </summary>
public class OtsWeeklyService(
    ScheduledJobService jobs,
    OtsPoAllocationService otsSvc)
{
    public const string JobName = "OtsWeekly";

    public Task<bool> IsActiveAsync(CancellationToken ct = default) =>
        jobs.IsActiveAsync(JobName, ScheduledJobService.SingleRowKey, ct);

    /// <summary>
    /// Runs Generate OTS for the current GST month/year and writes one
    /// dbo.WmsRptJobRun row. Never throws — the outcome is in the returned tuple
    /// and in the run log, so a timer caller does not need its own try/catch.
    /// </summary>
    /// <returns>(rows persisted, error message or null).</returns>
    public async Task<(int Rows, string? Error)> RunOnceAsync(
        string mode, string triggeredBy, CancellationToken ct = default)
    {
        var nowGst = DateTime.UtcNow.AddHours(4);
        var runId  = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        try
        {
            var (rows, warnings) = await otsSvc.GenerateAndPersistAsync(nowGst.Month, nowGst.Year, ct);

            // Warnings are non-fatal (missing weights, unmatched divisions, …) but
            // they explain a surprising row count, so surface them in the run log.
            var note = warnings.Count == 0 ? null : string.Join(" | ", warnings.Take(5));
            await jobs.FinishRunAsync(runId, "Success", rows, note, ct);
            return (rows, null);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            return (0, ex.Message);
        }
    }
}
