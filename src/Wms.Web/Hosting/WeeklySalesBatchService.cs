using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every day at 01:00 GST (Arabian Standard Time = UTC+04:00).
/// For each ACTIVE country in WmsRptCountryConfig (scoped to JobName
/// 'WeeklySalesFromGCP'), pulls the full weekly-sales feed from BigQuery once
/// and MERGE-upserts it into that country's on-prem LPM_Weekly_SalesAmt, logging
/// each run into WmsRptJobRun. Lives in-process; relies on App Service Always On
/// to be present at fire time.
/// </summary>
// TEMP TEST MODE — fires every 15 minutes instead of daily 01:00 GST, and skips the
// "already fired today" guard, so the ADC cold-start retry fix can be verified without
// waiting for the next real 01:00 fire. REVERT before merging to main: restore the
// daily 01:00 GST loop and the HasFiredTodayAsync guard in RunWeeklyAsync below.
public class WeeklySalesBatchService(IServiceProvider sp, ILogger<WeeklySalesBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan TestFireInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklySalesBatchService started. TEST MODE: firing every {Interval}.", TestFireInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                log.LogInformation("WeeklySalesBatchService: firing test run at {Now} UTC.", DateTime.UtcNow);
                await RunWeeklyAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "WeeklySalesBatchService: unexpected error in loop.");
            }

            try { await Task.Delay(TestFireInterval, stoppingToken); } catch { break; }
        }
    }

    private async Task RunWeeklyAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<WeeklySalesFromGcpService>();

        // TEMP TEST MODE — HasFiredTodayAsync guard skipped so the 15-minute test
        // interval can actually re-fire; restore it when reverting to the daily schedule.

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
                var runId = await svc.StartJobRunAsync("Daily", country, "Timer", ct);
                await svc.FinishJobRunAsync(runId, "Failed", null, 0, ex.Message, CancellationToken.None);
            }
            return;
        }

        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) return;
            var runId = await svc.StartJobRunAsync("Daily", country, "Timer", ct);
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
