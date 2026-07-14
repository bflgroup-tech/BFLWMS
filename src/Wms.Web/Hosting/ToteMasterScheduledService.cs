using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires once a day at 05:00 GST (Arabian Standard Time = UTC+04:00).
/// Calls ContainerAllocationDataSyncService.SyncToteIDMasterAsync with
/// origin="Scheduled" so the row appears in Recent Activity distinguishable
/// from a Manual UI click. Lives in-process; relies on App Service Always On
/// (same requirement as NightlyBatchService).
/// </summary>
public class ToteMasterScheduledService(IServiceProvider sp, ILogger<ToteMasterScheduledService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(5, 0, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("ToteMasterScheduledService started. Fire time: 05:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GstTz);
                var todayFireGst = nowGst.Date.Add(FireTimeGst);
                var nextFireGst  = nowGst >= todayFireGst ? todayFireGst.AddDays(1) : todayFireGst;

                var shouldFireNow = nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("ToteMasterScheduledService: firing scheduled tote sync at {Now}.", nowGst);
                    await RunAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
                }

                var sleep = TimeZoneInfo.ConvertTimeToUtc(nextFireGst, GstTz) - DateTime.UtcNow;
                if (sleep > TimeSpan.FromHours(1)) sleep = TimeSpan.FromHours(1);
                if (sleep < TimeSpan.FromSeconds(30)) sleep = TimeSpan.FromSeconds(30);
                await Task.Delay(sleep, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "ToteMasterScheduledService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ContainerAllocationDataSyncService>();
        try
        {
            var results = await svc.SyncToteIDMasterAsync(origin: "Scheduled", actor: "system (scheduled)", ct: ct);
            log.LogInformation("ToteMasterScheduledService: completed {N} country row(s).", results.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ToteMasterScheduledService: SyncToteIDMasterAsync threw.");
        }
    }
}
