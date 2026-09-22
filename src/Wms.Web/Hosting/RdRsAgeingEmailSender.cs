using System.Net;
using System.Net.Mail;
using System.Text;
using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Builds and sends the weekly RD/RS ageing email. Split out from the scheduled
/// service so the admin page can fire an on-demand send with the current config,
/// exactly as PendingGoodsReceiptEmailSender does.
/// </summary>
public class RdRsAgeingEmailSender(
    RdRsAgeingService ageing,
    IConfiguration cfg)
{
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    public record SendResult(int Rows, long TotalQty, bool Sent, string StatusMessage);

    /// <summary>
    /// Query, build, and send. With no rows the mail is skipped rather than sent
    /// empty — an empty RD/RS table means nothing is ageing, which is not news.
    /// Throws on query / SMTP failure so the caller can record the error.
    /// </summary>
    public async Task<SendResult> SendNowAsync(string recipientsCsv, string? ccCsv, CancellationToken ct = default)
    {
        var rows = await ageing.GetAgeingAsync(ct);
        if (rows.Count == 0)
            return new SendResult(0, 0, false, "skipped: no RD/RS boxes");

        var totalQty = rows.Sum(r => r.TotalQty);
        var (subject, html) = BuildEmail(rows);
        await SendAsync(recipientsCsv, ccCsv, subject, html, ct);
        return new SendResult(rows.Count, totalQty, true, "sent");
    }

    /// <summary>The preview the admin page renders, so what is on screen is what goes out.</summary>
    public async Task<(string Subject, string Html, int Rows, long TotalQty)> PreviewAsync(CancellationToken ct = default)
    {
        var rows = await ageing.GetAgeingAsync(ct);
        var (subject, html) = BuildEmail(rows);
        return (subject, html, rows.Count, rows.Sum(r => r.TotalQty));
    }

    public static (string Subject, string Html) BuildEmail(List<RdRsAgeingRow> rows)
    {
        var nowGst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GstTz);
        var totalQty = rows.Sum(r => r.TotalQty);
        var subject = $"RD / RS Ageing — {totalQty:N0} pcs across {rows.Count} division line(s) as of {nowGst:dd/MM/yyyy} GST";

        var sb = new StringBuilder();
        sb.Append("<html><body style=\"font-family:Segoe UI, Arial, sans-serif; font-size:13px; color:#111;\">");
        sb.Append("<p style=\"margin:0 0 12px 0\">Hi,</p>");
        sb.Append("<p style=\"margin:0 0 14px 0\">Please find the average days below. This report has been updated today.</p>");

        if (rows.Count == 0)
        {
            sb.Append("<p style=\"margin:0 0 16px 0; color:#166534; font-style:italic\">No RD or RS stock in the warehouse.</p>");
        }
        else
        {
            sb.Append("<table cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse; border:1px solid #2f5496\">");
            sb.Append("<thead style=\"background:#4472C4; color:#fff\"><tr>");
            sb.Append("<th style=\"text-align:left;   padding:6px 10px; border:1px solid #2f5496\">PalletType</th>");
            sb.Append("<th style=\"text-align:left;   padding:6px 10px; border:1px solid #2f5496\">Typename</th>");
            sb.Append("<th style=\"text-align:left;   padding:6px 10px; border:1px solid #2f5496\">Division</th>");
            sb.Append("<th style=\"text-align:right;  padding:6px 10px; border:1px solid #2f5496\">Total Qty</th>");
            sb.Append("<th style=\"text-align:right;  padding:6px 10px; border:1px solid #2f5496\">Avg Age (Days)</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var r in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7\">{Html(r.PalletType)}</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7\">{Html(r.TypeName ?? "")}</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7\">{Html(r.Division)}</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7; text-align:right\">{r.TotalQty:N0}</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7; text-align:right\">{r.AvgAgeDays:N1}</td>");
                sb.Append("</tr>");
            }

            // Per-pallet-type subtotals, so the two halves can be read at a glance
            // without adding up twenty division rows by eye.
            foreach (var g in rows.GroupBy(r => r.PalletType).OrderBy(g => g.Key))
            {
                var qty = g.Sum(x => x.TotalQty);
                // Weighted by qty: a 3-pc division at 200 days must not pull the
                // average as hard as a 9,000-pc division at 100.
                var avg = qty > 0 ? g.Sum(x => x.AvgAgeDays * x.TotalQty) / qty : 0m;
                sb.Append("<tr style=\"background:#d9e2f3; font-weight:bold\">");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7\" colspan=\"3\">{Html(g.Key)} total ({Html(g.First().TypeName ?? "")})</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7; text-align:right\">{qty:N0}</td>");
                sb.Append($"<td style=\"padding:6px 10px; border:1px solid #b4c6e7; text-align:right\">{avg:N1}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</tbody></table>");
        }

        sb.Append("<p style=\"margin-top:16px; color:#888; font-size:11px\">Automated notification from BFLWMS — RD/RS stock in <code>racks.dbo.whboxitems</code>, division by item, age from the box created date. Manage recipients and schedule at Admin &rarr; RD/RS Ageing Email.</p>");
        sb.Append("</body></html>");
        return (subject, sb.ToString());
    }

    private static string Html(string s) => WebUtility.HtmlEncode(s ?? "");

    private async Task SendAsync(string recipientsCsv, string? ccCsv, string subject, string html, CancellationToken ct)
    {
        var host    = cfg["Smtp:Host"] ?? throw new InvalidOperationException("Smtp:Host not configured.");
        var user    = cfg["Smtp:User"] ?? throw new InvalidOperationException("Smtp:User not configured.");
        var pass    = cfg["Smtp:Password"] ?? "";
        var useSsl  = bool.TryParse(cfg["Smtp:UseSsl"], out var ssl) && ssl;
        var fromNm  = cfg["Smtp:FromName"] ?? "BFLWMS";
        var port    = int.TryParse(cfg["Smtp:Port"], out var p) ? p : 587;

        using var msg = new MailMessage
        {
            From = new MailAddress(user, fromNm),
            Subject = subject,
            Body = html,
            IsBodyHtml = true,
        };

        static IEnumerable<string> Split(string? csv) =>
            (csv ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var addr in Split(recipientsCsv)) msg.To.Add(addr);
        foreach (var addr in Split(ccCsv))         msg.CC.Add(addr);
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
