using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires EVERY DAY at 11:15 GST (Arabian Standard Time = UTC+04:00). For each
/// ACTIVE country in WmsRptCountryConfig (scoped to JobName
/// 'WhStockLastDayFromGCP'), pulls the full wh_stock_last_day feed from BigQuery
/// once, then MERGE-upserts each active country's slice into
/// dbo.WMS_WHSTOCK_LASTDAY, logging each run into WmsRptJobRun. The source is a
/// monthly snapshot, so most daily runs just re-upsert the same rows (a harmless
/// no-op MERGE) until the month rolls over. Lives in-process; relies on App
/// Service Always On to be present at fire time.
///
/// RunDailyAsync checks ScheduledJobService.HasSuccessfulRunTodayAsync and takes
/// TryAcquireJobLockAsync before doing any work -- same fix applied to
/// OtsWeeklyService/VolumeGroupWeeklyService/IncreffSohFromGCP after a scale-out
/// event produced duplicate rows: every App Service instance runs every
/// HostedService, and an in-process "fired today" flag alone resets on every
/// deploy restart and can't see another instance's run.
/// </summary>
public class WhStockLastDayBatchService(IServiceProvider sp, ILogger<WhStockLastDayBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(11, 15, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WhStockLastDayBatchService started. Fire: daily 11:15 GST.");
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
                    log.LogInformation("WhStockLastDayBatchService: firing daily run at {Now}.", nowGst);
                    await RunDailyAsync(stoppingToken);
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
                log.LogError(ex, "WhStockLastDayBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunDailyAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();
        var svc = scope.ServiceProvider.GetRequiredService<WhStockLastDayFromGcpService>();

        // A deploy restart resets this timer's in-process "fired today" flag, so
        // without this check every restart after 11:15 would re-run the pull.
        if (await jobs.HasSuccessfulRunTodayAsync(WhStockLastDayFromGcpService.JobName, ct))
        {
            log.LogInformation("WhStockLastDayBatchService: already succeeded today — nothing to do.");
            return;
        }

        var countries = await jobs.GetActiveCountriesAsync(WhStockLastDayFromGcpService.JobName, ct);
        if (countries.Count == 0)
        {
            log.LogInformation("WhStockLastDayBatchService: no active countries — nothing to do.");
            return;
        }

        // Every App Service instance runs this HostedService — skip rather than
        // duplicate the BigQuery pull if another instance is already running it.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(WhStockLastDayFromGcpService.JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(WhStockLastDayFromGcpService.JobName, "Daily", null, "Timer", ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid a duplicate pull.", ct);
            return;
        }

        List<WhStockLastDayGcpRow> rows;
        try
        {
            rows = await svc.FetchFromBigQueryAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WhStockLastDayBatchService: BigQuery fetch FAILED — skipping all countries this run.");
            foreach (var country in countries)
            {
                var runId = await jobs.StartRunAsync(WhStockLastDayFromGcpService.JobName, "Daily", country, "Timer", ct);
                await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            }
            return;
        }

        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) return;
            var runId = await jobs.StartRunAsync(WhStockLastDayFromGcpService.JobName, "Daily", country, "Timer", ct);
            try
            {
                var written = await svc.UpsertRowsAsync(country, rows, ct);
                await jobs.FinishRunAsync(runId, "Success", written, null, ct);
                log.LogInformation("WhStockLastDayBatchService: {Country} done — {Rows} rows.", country, written);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "WhStockLastDayBatchService: {Country} FAILED.", country);
                await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            }
        }
    }
}
