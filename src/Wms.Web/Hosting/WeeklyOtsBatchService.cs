using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every Monday at 05:00 GST (Arabian Standard Time = UTC+04:00) — one hour
/// after WeeklyVolumeGroupBatchService, so Generate OTS reads the Volume Groups
/// that run refreshed rather than last week's snapshot.
///
/// The one-hour gap is a deliberate fixed offset, not a chain: this service does
/// not wait on the VG run. If VG ever overruns past 05:00, OTS will hit the
/// "Volume Group has not been generated today" precondition inside
/// GenerateAndPersistAsync and log a Failed run rather than silently computing
/// from a stale snapshot — which is the intended, visible failure.
///
/// Gated on the dbo.WmsRptCountryConfig row (JobName='OtsWeekly', Country='');
/// missing or inactive means the loop no-ops. Lives in-process, relies on
/// App Service Always On — same shape as WeeklyVolumeGroupBatchService.
/// </summary>
public class WeeklyOtsBatchService(IServiceProvider sp, ILogger<WeeklyOtsBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(5, 0, 0);
    private const DayOfWeek FireDay = DayOfWeek.Monday;
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklyOtsBatchService started. Fire: Monday 05:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowGst      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
                var nextFireGst = NextMondayFireGst(nowGst);

                // Monday, past 05:00, and not yet fired for today's date — covers an
                // app restart that lands mid-window.
                var todayFireGst  = nowGst.Date.Add(FireTimeGst);
                var shouldFireNow = nowGst.DayOfWeek == FireDay
                    && nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("WeeklyOtsBatchService: firing weekly run at {Now}.", nowGst);
                    await RunOnceAsync(stoppingToken);
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
                log.LogError(ex, "WeeklyOtsBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    /// <summary>Returns the next Monday-05:00 GST fire strictly in the future.</summary>
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
        var svc = scope.ServiceProvider.GetRequiredService<OtsWeeklyService>();

        if (!await svc.IsActiveAsync(ct))
        {
            log.LogInformation("WeeklyOtsBatchService: job is inactive — nothing to do.");
            return;
        }

        var (rows, err) = await svc.RunOnceAsync("Weekly", "Timer", ct);
        if (err is null) log.LogInformation("WeeklyOtsBatchService: {Rows} rows persisted.", rows);
        else             log.LogError("WeeklyOtsBatchService: FAILED — {Error}", err);
    }
}
