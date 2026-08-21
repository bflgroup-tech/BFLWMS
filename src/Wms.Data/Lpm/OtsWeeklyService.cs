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
    /// Has the BFLGROUP Volume Group run landed today? The timer checks this before
    /// firing so a mid-Monday restart — where every batch's catch-up path triggers
    /// in the same second — defers instead of racing VG and logging a guaranteed
    /// failure.
    /// </summary>
    public Task<bool> IsVolumeGroupReadyAsync(CancellationToken ct = default) =>
        otsSvc.IsVolumeGroupGeneratedTodayAsync(ct);

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

        // Already done today? A deploy restart resets the timer's in-process "fired
        // today" flag, so without this every deploy after 07:00 re-ran the whole
        // generate. Only Timer runs are gated — an operator clicking Run Now means it
        // deliberately, and should not be silently ignored.
        if (triggeredBy == "Timer" && await jobs.HasSuccessfulRunTodayAsync(JobName, ct))
            return (0, null);

        // One instance only. Every App Service instance runs this HostedService, and
        // Generate is DELETE-then-INSERT, so two concurrent runs interleave and leave
        // duplicate rows per (OTSDate, StoreID, DivCode). Skip rather than queue —
        // the other instance is already doing exactly this work.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate rows.", ct);
            return (0, null);
        }

        var runId  = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        try
        {
            var actor = triggeredBy == "Timer" ? "system (scheduled)" : triggeredBy;
            var (rows, warnings) = await otsSvc.GenerateAndPersistAsync(nowGst.Month, nowGst.Year, ct, actor);

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
