using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires EVERY DAY at 08:15 GST (Arabian Standard Time = UTC+04:00) — a fixed
/// 15-minute offset after IncreffSohFromGcpBatchService, so the compare reads a
/// freshly-refreshed dbo.LPM_ECOM_INCREFF_SOH rather than yesterday's data.
///
/// The offset is deliberate, not a wait-chain: this service does not wait on the
/// INCREFF pull. It checks readiness before firing instead, because the catch-up
/// path (an app restart later in the day) makes the offset meaningless — every
/// batch's "crossed my fire time and haven't run today" branch would otherwise
/// trigger in the same second, racing IncreffSohFromGCP rather than following it.
/// When that job hasn't succeeded today the fire is DEFERRED (lastFireGstDate is
/// left unset) and retried on the next wake, at most an hour later.
///
/// Gated on the dbo.WmsRptCountryConfig row (JobName='IncreffMfcsSohCompare',
/// Country=''); missing or inactive means the loop no-ops. Lives in-process,
/// relies on App Service Always On — same shape as WeeklyOtsBatchService.
/// </summary>
public class IncreffMfcsSohCompareBatchService(IServiceProvider sp, ILogger<IncreffMfcsSohCompareBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(8, 15, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("IncreffMfcsSohCompareBatchService started. Fire: daily 08:15 GST (after IncreffSohFromGCP).");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
                var todayFireGst = nowGst.Date.Add(FireTimeGst);
                var shouldFireNow = nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("IncreffMfcsSohCompareBatchService: firing daily run at {Now}.", nowGst);
                    // Leaving lastFireGstDate unset on a deferral is what makes the
                    // next wake retry — do not hoist this assignment.
                    if (await RunOnceAsync(stoppingToken))
                    {
                        lastFireGstDate = nowGst.Date;
                        continue;
                    }
                }

                var nextFireGst = todayFireGst <= nowGst ? todayFireGst.AddDays(1) : todayFireGst;
                var sleep = TimeZoneInfo.ConvertTimeToUtc(nextFireGst, GstTz) - DateTime.UtcNow;
                if (sleep > TimeSpan.FromHours(1)) sleep = TimeSpan.FromHours(1);
                if (sleep < TimeSpan.FromSeconds(30)) sleep = TimeSpan.FromSeconds(30);
                await Task.Delay(sleep, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "IncreffMfcsSohCompareBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    /// <returns>
    /// false when the fire was DEFERRED and should be retried on the next wake;
    /// true when it was handled (ran, or was skipped because the job is inactive).
    /// </returns>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();
        var svc = scope.ServiceProvider.GetRequiredService<IncreffMfcsSohCompareService>();

        if (!await jobs.IsActiveAsync(IncreffMfcsSohCompareService.JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogInformation("IncreffMfcsSohCompareBatchService: job is inactive — nothing to do.");
            return true;
        }

        if (!await jobs.HasSucceededTodayAsync(IncreffSohFromGcpService.JobName, ct))
        {
            log.LogWarning("IncreffMfcsSohCompareBatchService: IncreffSohFromGCP has not succeeded today yet — deferring, will retry on the next wake.");
            return false;
        }

        var runId = await jobs.StartRunAsync(IncreffMfcsSohCompareService.JobName, "Daily", null, "Timer", ct);
        try
        {
            var rows = await svc.RefreshAsync(ct);
            await jobs.FinishRunAsync(runId, "Success", rows, null, ct);
            log.LogInformation("IncreffMfcsSohCompareBatchService: {Rows} rows.", rows);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            log.LogError(ex, "IncreffMfcsSohCompareBatchService: FAILED.");
        }
        return true;
    }
}
