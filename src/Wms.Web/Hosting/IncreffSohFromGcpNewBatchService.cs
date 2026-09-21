using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires EVERY DAY at 08:05 GST (Arabian Standard Time = UTC+04:00) — 5 minutes
/// after IncreffSohFromGcpBatchService's 08:00 GST pull, so the two independent
/// BigQuery pulls don't contend, while both still pull a comparable "yesterday
/// GST" snapshot for validation against each other. Pulls yesterday's ECOM SOH
/// from the newer Silver-tier BigQuery source (IncreffSohFromGcpNewService.
/// RefreshAsync) and overwrites dbo.LPM_ECOM_INCREFF_SOH_NEW — a SEPARATE table
/// from dbo.LPM_ECOM_INCREFF_SOH, so this job cannot affect the live ECOM Stock
/// Variance Report while its new source is being validated.
///
/// Gated on the dbo.WmsRptCountryConfig row (JobName='IncreffSohFromGCP_New',
/// Country=''); missing or inactive means the loop no-ops. Lives in-process,
/// relies on App Service Always On — same shape as IncreffSohFromGcpBatchService.
///
/// RunOnceAsync re-checks ScheduledJobService.HasSuccessfulRunTodayAsync and
/// takes TryAcquireJobLockAsync before doing any work — same fix applied to
/// OtsWeeklyService/VolumeGroupWeeklyService after a scale-out event produced
/// duplicate rows: every App Service instance runs every HostedService, and
/// this timer's in-process "fired today" flag resets on every deploy restart.
/// </summary>
public class IncreffSohFromGcpNewBatchService(IServiceProvider sp, ILogger<IncreffSohFromGcpNewBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(8, 5, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("IncreffSohFromGcpNewBatchService started. Fire: daily 08:05 GST.");
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
                    log.LogInformation("IncreffSohFromGcpNewBatchService: firing daily run at {Now}.", nowGst);
                    await RunOnceAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
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
                log.LogError(ex, "IncreffSohFromGcpNewBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();
        var svc = scope.ServiceProvider.GetRequiredService<IncreffSohFromGcpNewService>();

        if (!await jobs.IsActiveAsync(IncreffSohFromGcpNewService.JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogInformation("IncreffSohFromGcpNewBatchService: job is inactive — nothing to do.");
            return;
        }

        // A deploy restart resets this timer's in-process "fired today" flag, so
        // without this check every restart after 08:05 would re-run the pull.
        if (await jobs.HasSuccessfulRunTodayAsync(IncreffSohFromGcpNewService.JobName, ct))
        {
            log.LogInformation("IncreffSohFromGcpNewBatchService: already succeeded today — nothing to do.");
            return;
        }

        // Every App Service instance runs this HostedService — skip rather than
        // duplicate the BigQuery pull if another instance is already running it.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(IncreffSohFromGcpNewService.JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(IncreffSohFromGcpNewService.JobName, "Daily", null, "Timer", ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid a duplicate pull.", ct);
            return;
        }

        var runId = await jobs.StartRunAsync(IncreffSohFromGcpNewService.JobName, "Daily", null, "Timer", ct);
        try
        {
            var rows = await svc.RefreshAsync(ct);
            await jobs.FinishRunAsync(runId, "Success", rows, null, ct);
            log.LogInformation("IncreffSohFromGcpNewBatchService: {Rows} rows.", rows);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            log.LogError(ex, "IncreffSohFromGcpNewBatchService: FAILED.");
        }
    }
}
