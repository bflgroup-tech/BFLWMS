using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every Monday at 07:00 GST (Arabian Standard Time = UTC+04:00) — one hour
/// after WeeklyVolumeGroupBatchService, so Generate OTS reads the Volume Groups
/// that run refreshed rather than last week's snapshot.
///
/// The one-hour gap is a deliberate fixed offset, not a chain: this service does
/// not wait on the VG run. But it does check readiness before firing, because the
/// catch-up path makes the offset meaningless — on an app restart late on a Monday
/// every batch's "crossed my fire time and haven't run today" branch triggers in
/// the same second, so OTS would otherwise start alongside VG rather than after it.
/// When VG has not landed yet the fire is DEFERRED (lastFireGstDate is left unset)
/// and retried on the next wake, at most an hour later.
///
/// A genuine VG failure still surfaces: once VG stops retrying, OTS keeps deferring
/// and simply never records a run for that Monday, with a warning per wake.
///
/// Gated on the dbo.WmsRptCountryConfig row (JobName='OtsWeekly', Country='');
/// missing or inactive means the loop no-ops. Lives in-process, relies on
/// App Service Always On — same shape as WeeklyVolumeGroupBatchService.
/// </summary>
public class WeeklyOtsBatchService(IServiceProvider sp, ILogger<WeeklyOtsBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(7, 0, 0);
    private const DayOfWeek FireDay = DayOfWeek.Monday;
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WeeklyOtsBatchService started. Fire: Monday 07:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowGst      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
                var nextFireGst = NextMondayFireGst(nowGst);

                // Monday, past 07:00, and not yet fired for today's date — covers an
                // app restart that lands mid-window.
                var todayFireGst  = nowGst.Date.Add(FireTimeGst);
                var shouldFireNow = nowGst.DayOfWeek == FireDay
                    && nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("WeeklyOtsBatchService: firing weekly run at {Now}.", nowGst);
                    // Leaving lastFireGstDate unset on a deferral is what makes the
                    // next wake retry — do not hoist this assignment.
                    if (await RunOnceAsync(stoppingToken))
                    {
                        lastFireGstDate = nowGst.Date;
                        continue;
                    }
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

    /// <summary>Returns the next Monday-07:00 GST fire strictly in the future.</summary>
    private static DateTime NextMondayFireGst(DateTime nowGst)
    {
        var daysToMonday = ((int)FireDay - (int)nowGst.DayOfWeek + 7) % 7;
        var candidate = nowGst.Date.AddDays(daysToMonday).Add(FireTimeGst);
        if (candidate <= nowGst) candidate = candidate.AddDays(7);
        return candidate;
    }

    /// <returns>
    /// false when the fire was DEFERRED and should be retried on the next wake;
    /// true when it was handled (ran, or was skipped because the job is inactive).
    /// </returns>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<OtsWeeklyService>();

        if (!await svc.IsActiveAsync(ct))
        {
            log.LogInformation("WeeklyOtsBatchService: job is inactive — nothing to do.");
            return true;
        }

        if (!await svc.IsVolumeGroupReadyAsync(ct))
        {
            log.LogWarning("WeeklyOtsBatchService: Volume Group has not been generated today — deferring, will retry on the next wake.");
            return false;
        }

        var (rows, err) = await svc.RunOnceAsync("Weekly", "Timer", ct);
        if (err is null) log.LogInformation("WeeklyOtsBatchService: {Rows} rows persisted.", rows);
        else             log.LogError("WeeklyOtsBatchService: FAILED — {Error}", err);
        return true;
    }
}
