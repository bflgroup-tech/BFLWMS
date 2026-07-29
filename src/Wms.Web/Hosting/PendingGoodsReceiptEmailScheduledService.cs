using Wms.Data.Notifications;

namespace Wms.Web.Hosting;

/// <summary>
/// Hourly (or configured cadence) mailer for the Pending Goods Receipt report.
/// Polls dbo.WmsPendingGoodsReceiptEmailConfig every minute; when IsActive and
/// (now - LastRunTS) >= IntervalHours, delegates to PendingGoodsReceiptEmailSender
/// to build + send. Skips silently when both sections are empty.
///
/// Same in-process shape as ToteMasterScheduledService — relies on App Service
/// Always On to keep the loop alive.
/// </summary>
public class PendingGoodsReceiptEmailScheduledService(
    IServiceProvider sp,
    ILogger<PendingGoodsReceiptEmailScheduledService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("PendingGoodsReceiptEmailScheduledService started (polling every 60s).");
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
                log.LogError(ex, "PendingGoodsReceiptEmailScheduledService: loop error; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc    = scope.ServiceProvider.GetRequiredService<PendingGoodsReceiptEmailService>();
        var sender = scope.ServiceProvider.GetRequiredService<PendingGoodsReceiptEmailSender>();

        var conf = await svc.GetAsync(ct);
        if (conf is null || !conf.IsActive) return;
        if (string.IsNullOrWhiteSpace(conf.Recipients)) return;
        if (conf.IntervalHours <= 0) return;

        var due = conf.LastRunTS is null
                  || (DateTime.UtcNow - conf.LastRunTS.Value.AddHours(-4)) >= TimeSpan.FromHours(conf.IntervalHours);
        if (!due) return;

        try
        {
            var res = await sender.SendNowAsync(conf.Recipients, ct);
            await svc.RecordRunAsync(conf.Id, res.StatusMessage, res.Sent ? res.PendingCount : 0, ct);
            if (res.Sent)
                log.LogInformation("PendingGoodsReceiptEmail: sent {N} pending + {P} purchased to {Recipients}.", res.PendingCount, res.PurchasedCount, conf.Recipients);
            else
                log.LogInformation("PendingGoodsReceiptEmail: {Status}.", res.StatusMessage);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            try { await svc.RecordRunAsync(conf.Id, "error: " + msg, null, ct); } catch { }
            log.LogError(ex, "PendingGoodsReceiptEmail: send failed.");
        }
    }
}
