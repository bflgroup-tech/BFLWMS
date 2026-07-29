using System.Net;
using System.Net.Mail;
using System.Text;
using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Builds and sends the Pending Goods Receipt email. Extracted from
/// PendingGoodsReceiptEmailScheduledService so the admin page can also
/// trigger an on-demand send ("Send Test Email") using the current SMTP
/// config and recipients list.
/// </summary>
public class PendingGoodsReceiptEmailSender(
    CountingReportsService reports,
    IConfiguration cfg)
{
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    /// <summary>Result of a send attempt: how many rows were in each section, and whether the mail actually went out.</summary>
    public record SendResult(int PendingCount, int PurchasedCount, bool Sent, string StatusMessage);

    /// <summary>
    /// Query both sections, build the HTML, and (if any rows exist) send to
    /// <paramref name="recipientsCsv"/>. When both sections are empty, returns
    /// with Sent=false and a "skipped" status — mirrors the scheduled service.
    /// Throws on SMTP / query failure so the caller can surface the error.
    /// </summary>
    public async Task<SendResult> SendNowAsync(string recipientsCsv, CancellationToken ct = default)
    {
        var rows      = await reports.GetPendingPurchaseAsync(ct);
        var purchased = await reports.GetPurchasedContainersAsync(ct);
        if (rows.Count == 0 && purchased.Count == 0)
            return new SendResult(0, 0, false, "skipped: no pending or purchased containers");

        var (subject, html) = BuildEmail(rows, purchased);
        await SendAsync(recipientsCsv, subject, html, ct);
        return new SendResult(rows.Count, purchased.Count, true, "sent");
    }

    public static (string Subject, string Html) BuildEmail(List<PendingPurchaseRow> rows, List<PurchasedContainerRow> purchased)
    {
        var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
        var subject = $"Pending Goods Receipt — {rows.Count} pending, {purchased.Count} purchased as of {nowGst:dd/MM/yyyy HH:mm} GST";

        var sb = new StringBuilder();
        sb.Append("<html><body style=\"font-family:Segoe UI, Arial, sans-serif; font-size:13px; color:#111;\">");

        // ---- Section 1: Pending ----
        sb.Append("<h2 style=\"margin:0 0 8px 0\">Pending Goods Receipt</h2>");
        sb.Append("<p style=\"margin:0 0 12px 0; color:#555\">Containers counted (<code>bfldata.dbo.BuildingCompletion</code>) from <b>01/01/2026</b> onwards whose GRN row has not yet landed in <code>usa.dbo.usapurchase</code>. Ageing = days since counting.</p>");
        sb.Append($"<p style=\"margin:0 0 12px 0\"><b>{rows.Count} container(s)</b> — {rows.Sum(r => r.CountedQty):N0} total counted qty.</p>");
        if (rows.Count == 0)
        {
            sb.Append("<p style=\"margin:0 0 16px 0; color:#166534; font-style:italic\">No pending containers — every counted container has a matching row in usa.usapurchase.</p>");
        }
        else
        {
            sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse; border:1px solid #ccc\">");
            sb.Append("<thead style=\"background:#111; color:#fff\"><tr>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">ContNo</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Counting Date</th>");
            sb.Append("<th style=\"text-align:center; padding:6px 10px\">Counted Qty</th>");
            sb.Append("<th style=\"text-align:center; padding:6px 10px\">Ageing Days</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Divisions</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var r in rows)
            {
                sb.Append("<tr style=\"border-top:1px solid #e5e7eb\">");
                sb.Append($"<td style=\"padding:6px 10px; font-family:Consolas,monospace\"><b>{Html(r.ContNo)}</b></td>");
                sb.Append($"<td style=\"padding:6px 10px\">{r.CountingDate:dd/MM/yyyy}</td>");
                sb.Append($"<td style=\"padding:6px 10px; text-align:center\">{r.CountedQty:N0}</td>");
                sb.Append($"<td style=\"padding:6px 10px; text-align:center\"><b>{r.AgeingDays}</b></td>");
                sb.Append($"<td style=\"padding:6px 10px\">{Html(r.Divisions)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }

        // ---- Section 2: Purchased ----
        sb.Append("<h2 style=\"margin:28px 0 8px 0\">Purchased Containers</h2>");
        sb.Append("<p style=\"margin:0 0 12px 0; color:#555\">Containers counted from <b>01/01/2026</b> onwards whose GRN row HAS landed in <code>usa.dbo.usapurchase</code>. Purchase date/time is the earliest usapurchase row for the container. Days to Purchase = counting → purchase lag.</p>");
        sb.Append($"<p style=\"margin:0 0 12px 0\"><b>{purchased.Count} container(s)</b> — {purchased.Sum(r => r.CountedQty):N0} total counted qty.</p>");
        if (purchased.Count == 0)
        {
            sb.Append("<p style=\"margin:0 0 16px 0; color:#888; font-style:italic\">No purchased containers in scope.</p>");
        }
        else
        {
            sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse; border:1px solid #ccc\">");
            sb.Append("<thead style=\"background:#111; color:#fff\"><tr>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">ContNo</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Counting Date</th>");
            sb.Append("<th style=\"text-align:center; padding:6px 10px\">Counted Qty</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Purchase Date</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Purchase Time</th>");
            sb.Append("<th style=\"text-align:center; padding:6px 10px\">Days to Purchase</th>");
            sb.Append("<th style=\"text-align:left; padding:6px 10px\">Divisions</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var r in purchased)
            {
                sb.Append("<tr style=\"border-top:1px solid #e5e7eb\">");
                sb.Append($"<td style=\"padding:6px 10px; font-family:Consolas,monospace\"><b>{Html(r.ContNo)}</b></td>");
                sb.Append($"<td style=\"padding:6px 10px\">{r.CountingDate:dd/MM/yyyy}</td>");
                sb.Append($"<td style=\"padding:6px 10px; text-align:center\">{r.CountedQty:N0}</td>");
                sb.Append($"<td style=\"padding:6px 10px\">{r.PurchaseDate:dd/MM/yyyy}</td>");
                sb.Append($"<td style=\"padding:6px 10px\">{Html(r.PurchaseTime ?? "")}</td>");
                sb.Append($"<td style=\"padding:6px 10px; text-align:center\"><b>{r.DaysToPurchase}</b></td>");
                sb.Append($"<td style=\"padding:6px 10px\">{Html(r.Divisions)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }

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
        if (msg.To.Count == 0) throw new InvalidOperationException("No valid recipient addresses in the Recipients list.");

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
