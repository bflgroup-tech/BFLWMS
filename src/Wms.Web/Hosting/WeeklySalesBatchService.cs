using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires once a week, Monday 01:00 GST (Arabian Standard Time = UTC+04:00).
/// For each ACTIVE country in WmsRptCountryConfig (scoped to JobName
/// 'WeeklySalesFromGCP'), pulls the full weekly-sales feed from BigQuery once
/// and MERGE-upserts it into that country's on-prem LPM_Weekly_SalesAmt, logging
/// each run into WmsRptJobRun. Lives in-process; relies on App Service Always On
/// to be present at fire time.
/// </summary>
public class WeeklySalesBatchService(IServiceProvider sp, ILogger<WeeklySalesBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(1, 0, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklySalesBatchService started. Fire time: Monday 01:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GstTz);
                var todayFireGst = nowGst.Date.Add(FireTimeGst);

                var shouldFireNow = nowGst.DayOfWeek == DayOfWeek.Monday
                    && nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("WeeklySalesBatchService: firing weekly run at {Now}.", nowGst);
                    await RunWeeklyAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
                }

                // Sleep up to 1 hour at a time (defensive wakeups) so a missed
                // Monday window (e.g. app restart) is still caught same-day.
                var sleep = TimeSpan.FromHours(1);
                await Task.Delay(sleep, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "WeeklySalesBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunWeeklyAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<WeeklySalesFromGcpService>();

        // A redeploy restarts the app mid-day, which resets ExecuteAsync's in-memory
        // "already fired today" tracker — recheck against the persisted job-run log so
        // a restart on a Monday doesn't trigger a second same-day BigQuery pull.
        var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
        if (await svc.HasFiredTodayAsync("Weekly", nowGst.Date, ct))
        {
            log.LogInformation("WeeklySalesBatchService: Weekly run already logged for today — skipping (likely a restart).");
            return;
        }

        var countries = await svc.GetActiveCountriesAsync(ct);
        if (countries.Count == 0)
        {
            log.LogInformation("WeeklySalesBatchService: no active countries — nothing to do.");
            return;
        }

        List<WeeklySalesGcpRow> rows;
        try
        {
            rows = await svc.FetchFromBigQueryAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WeeklySalesBatchService: BigQuery fetch FAILED — skipping all countries this run.");
            foreach (var country in countries)
            {
                var runId = await svc.StartJobRunAsync("Weekly", country, "Timer", ct);
                await svc.FinishJobRunAsync(runId, "Failed", null, 0, ex.Message, CancellationToken.None);
            }
            return;
        }

        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) return;
            var runId = await svc.StartJobRunAsync("Weekly", country, "Timer", ct);
            try
            {
                var written = await svc.UpsertRowsAsync(country, rows, ct);
                await svc.FinishJobRunAsync(runId, "Success", written, null, null, ct);
                log.LogInformation("WeeklySalesBatchService: {Country} done — {Rows} rows.", country, written);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "WeeklySalesBatchService: {Country} FAILED.", country);
                await svc.FinishJobRunAsync(runId, "Failed", null, 0, ex.Message, CancellationToken.None);
            }
        }
    }
}
