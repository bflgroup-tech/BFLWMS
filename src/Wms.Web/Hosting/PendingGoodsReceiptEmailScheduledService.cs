using System.Net;
using System.Net.Mail;
using System.Text;
using Wms.Data.Lpm;
using Wms.Data.Notifications;

namespace Wms.Web.Hosting;

/// <summary>
/// Hourly (or configured cadence) mailer for the Pending Goods Receipt report.
/// Polls dbo.WmsPendingGoodsReceiptEmailConfig every minute; when IsActive and
/// (now - LastRunTS) >= IntervalHours, queries the report and mails the HTML
/// table to Recipients. Skips silently when the query returns zero rows.
///
/// SMTP settings come from configuration keys Smtp:Host, Smtp:Port, Smtp:User,
/// Smtp:Password, Smtp:UseSsl, Smtp:FromName (Azure App Service uses double
/// underscores: Smtp__Host etc.). Failures land in LastRunStatus and are
/// logged; the loop then retries on the next cadence tick.
///
/// Same in-process shape as ToteMasterScheduledService — relies on App Service
/// Always On to keep the loop alive.
/// </summary>
public class PendingGoodsReceiptEmailScheduledService(
    IServiceProvider sp,
    IConfiguration cfg,
    ILogger<PendingGoodsReceiptEmailScheduledService> log)
    : BackgroundService
{
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

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
        var svc = scope.ServiceProvider.GetRequiredService<PendingGoodsReceiptEmailService>();
        var reports = scope.ServiceProvider.GetRequiredService<CountingReportsService>();

        var conf = await svc.GetAsync(ct);
        if (conf is null || !conf.IsActive) return;
        if (string.IsNullOrWhiteSpace(conf.Recipients)) return;
        if (conf.IntervalHours <= 0) return;

        var due = conf.LastRunTS is null
                  || (DateTime.UtcNow - conf.LastRunTS.Value.AddHours(-4)) >= TimeSpan.FromHours(conf.IntervalHours);
        if (!due) return;

        try
        {
            var rows = await reports.GetPendingPurchaseAsync(ct);
            if (rows.Count == 0)
            {
                await svc.RecordRunAsync(conf.Id, "skipped: no pending containers", 0, ct);
                log.LogInformation("PendingGoodsReceiptEmail: 0 rows, skipped send.");
                return;
            }

            var (subject, html) = BuildEmail(rows);
            await SendAsync(conf.Recipients, subject, html, ct);
            await svc.RecordRunAsync(conf.Id, "sent", rows.Count, ct);
            log.LogInformation("PendingGoodsReceiptEmail: sent {N} rows to {Recipients}.", rows.Count, conf.Recipients);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            try { await svc.RecordRunAsync(conf.Id, "error: " + msg, null, ct); } catch { }
            log.LogError(ex, "PendingGoodsReceiptEmail: send failed.");
        }
    }

    private static (string Subject, string Html) BuildEmail(List<PendingPurchaseRow> rows)
    {
        var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
        var subject = $"Pending Goods Receipt — {rows.Count} container(s) as of {nowGst:dd/MM/yyyy HH:mm} GST";

        var sb = new StringBuilder();
        sb.Append("<html><body style=\"font-family:Segoe UI, Arial, sans-serif; font-size:13px; color:#111;\">");
        sb.Append("<h2 style=\"margin:0 0 8px 0\">Pending Goods Receipt</h2>");
        sb.Append("<p style=\"margin:0 0 12px 0; color:#555\">Containers counted (<code>bfldata.dbo.BuildingCompletion</code>) from <b>01/01/2026</b> onwards whose GRN row has not yet landed in <code>usa.dbo.usapurchase</code>. Ageing = days since counting.</p>");
        sb.Append($"<p style=\"margin:0 0 12px 0\"><b>{rows.Count} container(s)</b> — {rows.Sum(r => r.CountedQty):N0} total counted qty.</p>");
        sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse; border:1px solid #ccc\">");
        sb.Append("<thead style=\"background:#111; color:#fff\"><tr>");
        sb.Append("<th style=\"text-align:left; padding:6px 10px\">ContNo</th>");
        sb.Append("<th style=\"text-align:left; padding:6px 10px\">Counting Date</th>");
        sb.Append("<th style=\"text-align:left; padding:6px 10px\">Time</th>");
        sb.Append("<th style=\"text-align:center; padding:6px 10px\">Counted Qty</th>");
        sb.Append("<th style=\"text-align:center; padding:6px 10px\">Ageing Days</th>");
        sb.Append("<th style=\"text-align:left; padding:6px 10px\">Divisions</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in rows)
        {
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb\">");
            sb.Append($"<td style=\"padding:6px 10px; font-family:Consolas,monospace\"><b>{Html(r.ContNo)}</b></td>");
            sb.Append($"<td style=\"padding:6px 10px\">{r.CountingDate:dd/MM/yyyy}</td>");
            sb.Append($"<td style=\"padding:6px 10px\">{Html(r.TrnTime ?? "")}</td>");
            sb.Append($"<td style=\"padding:6px 10px; text-align:center\">{r.CountedQty:N0}</td>");
            sb.Append($"<td style=\"padding:6px 10px; text-align:center\"><b>{r.AgeingDays}</b></td>");
            sb.Append($"<td style=\"padding:6px 10px\">{Html(r.Divisions)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        sb.Append("<p style=\"margin-top:16px; color:#888; font-size:11px\">Automated notification from BFLWMS. Manage recipients + schedule at Admin &rarr; Pending Goods Receipt Email.</p>");
        sb.Append("</body></html>");
        return (subject, sb.ToString());
    }

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private async Task SendAsync(string recipientsCsv, string subject, string html, CancellationToken ct)
    {
        var host    = cfg["Smtp:Host"] ?? throw new InvalidOperationException("Smtp:Host not configured.");
        var portStr = cfg["Smtp:Port"];
        var user    = cfg["Smtp:User"] ?? throw new InvalidOperationException("Smtp:User not configured.");
        var pass    = cfg["Smtp:Password"] ?? "";
        var useSsl  = bool.TryParse(cfg["Smtp:UseSsl"], out var ssl) && ssl;
        var fromNm  = cfg["Smtp:FromName"] ?? "BFLWMS";
        var port    = int.TryParse(portStr, out var p) ? p : 587;

        using var msg = new MailMessage
        {
            From = new MailAddress(user, fromNm),
            Subject = subject,
            Body = html,
            IsBodyHtml = true,
        };
        foreach (var addr in recipientsCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(addr);

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(user, pass),
        };
        await client.SendMailAsync(msg, ct);
    }
}
