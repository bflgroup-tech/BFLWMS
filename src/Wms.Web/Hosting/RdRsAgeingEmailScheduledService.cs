using Wms.Data.Notifications;

namespace Wms.Web.Hosting;

/// <summary>
/// Weekly mailer for the RD/RS ageing report. Polls
/// dbo.WmsRdRsAgeingEmailConfig every minute; fires when the GST clock is on the
/// configured day at or past the configured time and today has not already been
/// attempted.
///
/// Day + time rather than an interval, unlike PendingGoodsReceiptEmail: this one
/// has to land on Monday morning, not every N hours.
///
/// LastRunDate is stamped on every outcome — sent, skipped, or error — so a
/// failing send is attempted once that day rather than sixty times an hour. The
/// error is on the admin page either way.
///
/// "At or past" rather than "equals" so a deploy or restart across the scheduled
/// minute still sends that day instead of silently missing the week.
///
/// Same in-process shape as the other scheduled services — relies on App Service
/// Always On to keep the loop alive.
/// </summary>
public class RdRsAgeingEmailScheduledService(
    IServiceProvider sp,
    ILogger<RdRsAgeingEmailScheduledService> log)
    : BackgroundService
{
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("RdRsAgeingEmailScheduledService started (polling every 60s).");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "RdRsAgeingEmailScheduledService: loop error; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc    = scope.ServiceProvider.GetRequiredService<RdRsAgeingEmailService>();
        var sender = scope.ServiceProvider.GetRequiredService<RdRsAgeingEmailSender>();

        var conf = await svc.GetAsync(ct);
        if (conf is null || !conf.IsActive) return;
        if (string.IsNullOrWhiteSpace(conf.Recipients)) return;

        var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
        if ((int)nowGst.DayOfWeek != conf.DayOfWeek) return;

        var dueAt = nowGst.Date.AddHours(conf.HourGst).AddMinutes(conf.MinuteGst);
        if (nowGst < dueAt) return;
        if (conf.LastRunDate?.Date == nowGst.Date) return;   // already attempted today

        try
        {
            var res = await sender.SendNowAsync(conf.Recipients, conf.CcRecipients, ct);
            await svc.RecordRunAsync(conf.Id, nowGst, res.StatusMessage,
                res.Sent ? res.Rows : null, res.Sent ? res.TotalQty : null, ct);

            if (res.Sent)
                log.LogInformation("RdRsAgeingEmail: sent {Rows} row(s), {Qty} pcs to {Recipients}.",
                    res.Rows, res.TotalQty, conf.Recipients);
            else
                log.LogInformation("RdRsAgeingEmail: {Status}.", res.StatusMessage);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            try { await svc.RecordRunAsync(conf.Id, nowGst, "error: " + msg, null, null, ct); } catch { }
            log.LogError(ex, "RdRsAgeingEmail: send failed.");
        }
    }
}
