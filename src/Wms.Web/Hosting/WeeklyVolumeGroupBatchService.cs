using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires EVERY DAY at 06:00 GST (Arabian Standard Time = UTC+04:00).
/// Calls VolumeGroupWeeklyService.RunOnceAsync which loops each active
/// (JobName=VolumeGroupWeekly) country and refreshes Volume Groups for
/// the current month/year. In-process, relies on App Service Always On.
///
/// WeeklyOtsBatchService follows at 07:00 and depends on this run having
/// completed for BFLGROUP — move one and move the other.
///
/// NOTE the class and its JobName still read "Weekly". The cadence changed to
/// daily but JobName='VolumeGroupWeekly' is the key of the existing
/// dbo.WmsRptCountryConfig rows, so renaming it would orphan every country
/// toggle. Name kept deliberately.
/// </summary>
public class WeeklyVolumeGroupBatchService(IServiceProvider sp, ILogger<WeeklyVolumeGroupBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(6, 0, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklyVolumeGroupBatchService started. Fire: daily 06:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GstTz);
                var nextFireGst = NextFireGst(nowGst);

                // Crossed 06:00 today and haven't yet fired for today's date — fire
                // now (also covers an app restart landing mid-window).
                var todayFireGst = nowGst.Date.Add(FireTimeGst);
                var shouldFireNow = nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("WeeklyVolumeGroupBatchService: firing daily run at {Now}.", nowGst);
                    await RunOnceAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
                }

                // Sleep until the next 06:00 GST, capped at 1 hour so we
                // wake up regularly to handle clock changes / restarts.
                var sleep = TimeZoneInfo.ConvertTimeToUtc(nextFireGst, GstTz) - DateTime.UtcNow;
                if (sleep > TimeSpan.FromHours(1)) sleep = TimeSpan.FromHours(1);
                if (sleep < TimeSpan.FromSeconds(30)) sleep = TimeSpan.FromSeconds(30);
                await Task.Delay(sleep, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "WeeklyVolumeGroupBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    /// <summary>Returns the next 06:00 GST fire strictly in the future.</summary>
    private static DateTime NextFireGst(DateTime nowGst)
    {
        var candidate = nowGst.Date.Add(FireTimeGst);
        if (candidate <= nowGst) candidate = candidate.AddDays(1);
        return candidate;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<VolumeGroupWeeklyService>();

        var results = await svc.RunOnceAsync("Daily", "Timer", ct);
        if (results.Count == 0)
        {
            log.LogInformation("WeeklyVolumeGroupBatchService: no active countries — nothing to do.");
            return;
        }
        foreach (var (country, rows, err) in results)
        {
            if (err is null)
                log.LogInformation("WeeklyVolumeGroupBatchService: {Country} — {Rows} rows.", country, rows);
            else
                log.LogError("WeeklyVolumeGroupBatchService: {Country} FAILED — {Error}", country, err);
        }
    }
}
