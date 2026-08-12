using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every Monday at 04:00 GST (Arabian Standard Time = UTC+04:00).
/// Calls VolumeGroupWeeklyService.RunOnceAsync which loops each active
/// (JobName=VolumeGroupWeekly) country and refreshes Volume Groups for
/// the current month/year. In-process, relies on App Service Always On.
///
/// Mirrors NightlyBatchService's daily loop but with a week cadence.
/// </summary>
public class WeeklyVolumeGroupBatchService(IServiceProvider sp, ILogger<WeeklyVolumeGroupBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(4, 0, 0);
    private const DayOfWeek FireDay = DayOfWeek.Monday;
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklyVolumeGroupBatchService started. Fire: Monday 04:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GstTz);
                var nextFireGst = NextMondayFireGst(nowGst);

                // If it's Monday and we've crossed 04:00 today and haven't yet
                // fired for today's date, fire now (covers app-restart mid-window).
                var todayFireGst = nowGst.Date.Add(FireTimeGst);
                var shouldFireNow = nowGst.DayOfWeek == FireDay
                    && nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("WeeklyVolumeGroupBatchService: firing weekly run at {Now}.", nowGst);
                    await RunOnceAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
                }

                // Sleep until next Monday 04:00 GST, capped at 1 hour so we
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

    /// <summary>Returns the next Monday-04:00 GST fire strictly in the future.</summary>
    private static DateTime NextMondayFireGst(DateTime nowGst)
    {
        var daysToMonday = ((int)FireDay - (int)nowGst.DayOfWeek + 7) % 7;
        var candidate = nowGst.Date.AddDays(daysToMonday).Add(FireTimeGst);
        if (candidate <= nowGst) candidate = candidate.AddDays(7);
        return candidate;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<VolumeGroupWeeklyService>();

        var results = await svc.RunOnceAsync("Weekly", "Timer", ct);
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
