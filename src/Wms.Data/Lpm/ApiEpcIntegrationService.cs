using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public sealed class ApiEpcIntegrationOptions
{
    public const string SectionName = "ApiEpcIntegration";

    /// <summary>Sent as the raw "apikey" request header. Overridden in Program.cs
    /// from the top-level "apikey" App Service setting (shared with
    /// ApiGinIntegrationOptions — both Altavant endpoints take the same key), not
    /// read from the "ApiEpcIntegration" section above.</summary>
    public string ApiKey { get; set; } = "";
}

// Column aliases in SourceQuery are PascalCase to match these property names —
// Dapper maps them case-insensitively regardless, kept PascalCase for readability.
internal sealed record EpcSourceRow(
    string Srno, string? Site, string? SubLocation, string? Epc, string? Sku, string? LotNumber,
    int Quantity, string? Ean, string? Barcode, string? SerialNumber, string? Function,
    DateTime CreationDateUtc, string? Printer, bool? AntiTheft);

internal sealed record EpcApiItem(
    [property: JsonPropertyName("site")]          string Site,
    [property: JsonPropertyName("sub_location")]  string SubLocation,
    [property: JsonPropertyName("epc")]           string Epc,
    [property: JsonPropertyName("sku")]           string Sku,
    [property: JsonPropertyName("lot_number")]    string LotNumber,
    [property: JsonPropertyName("quantity")]      int    Quantity,
    [property: JsonPropertyName("ean")]           string Ean,
    [property: JsonPropertyName("barcode")]       string Barcode,
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("function")]      string Function,
    [property: JsonPropertyName("creation_date")] string CreationDate,
    [property: JsonPropertyName("printer")]       string Printer,
    [property: JsonPropertyName("anti_theft")]    bool?  AntiTheft);

internal sealed record EpcApiRequest([property: JsonPropertyName("items")] List<EpcApiItem> Items);

internal sealed record EpcApiResponse(
    [property: JsonPropertyName("batch_id")]     string? BatchId,
    [property: JsonPropertyName("status")]       string? Status,
    [property: JsonPropertyName("record_count")] int?    RecordCount);

/// <summary>
/// Pushes EPC/RFID tag events to the external API at
/// https://api.bfl.altavantconsulting.eu/v1/epc/imports (POST, API-key auth
/// header "apikey"), source query supplied 2026-09-24:
///
///   SELECT site        = (SELECT StoreID FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopName),
///          sub_location = (SELECT StoreID FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopName),
///          epc, sku = Itemcode, lot_number = '', quantity = 1, ean = ean13,
///          barcode = SerializedCode, serial_number = '', [function] = '',
///          creation_date = SYSUTCDATETIME(), printer = '', anti_theft = NULL
///     FROM DATAREPORTING.dbo.EPCBarcodes a
///    WHERE a.Srno NOT IN (SELECT SerializedCode FROM LPMSIM.dbo.EPCBarcodes_ApiCallDetails
///                           WHERE CreateTS >= DATEADD(day, -1, GETDATE()))
///
/// anti_theft is sent as JSON null (mapped from a nullable bool here), not '' —
/// the API rejected an empty string ("expected one of boolean, null, got
/// string") since the query has no real source column for it yet.
///
/// creation_date is formatted as "yyyy-MM-ddTHH:mm:ssZ" (matching the API's
/// documented example) rather than left to DateTime's default JSON
/// serialization — the API rejected the default round-trip format ("expect
/// valid date-time format but got: 2026-09-25T09:54:32.517") since it carries
/// fractional seconds and no UTC/offset marker. The source column also moved
/// from GETDATE() (SQL Server local time) to SYSUTCDATETIME(), since the 'Z'
/// suffix would otherwise be claiming a UTC time that it wasn't.
///
/// The "already sent" filter excludes rows whose Srno already appears in
/// LPMSIM.dbo.EPCBarcodes_ApiCallDetails within the last day. After each batch the
/// API accepts, that batch's rows are inserted there (Srno, SerializedCode, EPC,
/// GETDATE()), so a repeat run only picks up new rows. On-demand only ("Send Now"
/// on Nightly Batches) — no timer yet.
/// </summary>
public class ApiEpcIntegrationService(
    IOnPremConnectionResolver resolver, ScheduledJobService jobs, HttpClient http, IOptions<ApiEpcIntegrationOptions> opts)
{
    private const int CommandTimeoutSeconds = 60;
    public const string JobName = "APIEpcIntegration";
    private const string ApiUrl = "https://api.bfl.altavantconsulting.eu/v1/epc/imports";

    // The API rejects a request with more than 500 items ("expect array to have
    // at most 500 items"), so a full EPCBarcodes pull is sent in chunks.
    private const int MaxItemsPerRequest = 500;

    // OnPremBackup (the LOGBACKUP server), not WmsProductionDb — same switch as
    // ApiGinIntegrationService, whose WmsProductionDb connection didn't resolve
    // LPMSIM.dbo.APICallGIN ("Invalid object name") while OnPremBackup did.
    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private const string SourceQuery = @"
        SELECT Srno         = CAST(a.Srno AS VARCHAR(50)),
               Site         = (SELECT StoreID FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopName),
               SubLocation  = (SELECT StoreID FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopName),
               Epc          = a.EPC,
               Sku          = a.Itemcode,
               LotNumber    = '',
               Quantity     = 1,
               Ean          = a.ean13,
               Barcode      = a.SerializedCode,
               SerialNumber = '',
               [Function]   = '',
               CreationDateUtc = SYSUTCDATETIME(),
               Printer      = '',
               AntiTheft    = CAST(NULL AS BIT)
          FROM DATAREPORTING.dbo.EPCBarcodes a
         WHERE a.Srno NOT IN (
                   SELECT SerializedCode FROM LPMSIM.dbo.EPCBarcodes_ApiCallDetails
                    WHERE CreateTS >= DATEADD(day, -1, GETDATE()))";

    // Same shape as the supplied statement, but scoped to the Srnos of one batch
    // the API just accepted, so a failed batch is never recorded as sent.
    private const string MarkSentSql = @"
        INSERT INTO LPMSIM.dbo.EPCBarcodes_ApiCallDetails
        SELECT a.Srno, a.SerializedCode, a.EPC, GETDATE()
          FROM DATAREPORTING.dbo.EPCBarcodes a
         WHERE a.Srno IN @srnos
           AND a.Srno NOT IN (
                   SELECT SerializedCode FROM LPMSIM.dbo.EPCBarcodes_ApiCallDetails
                    WHERE CreateTS >= DATEADD(day, -1, GETDATE()))";

    /// <summary>
    /// Reads not-yet-sent rows from DATAREPORTING.dbo.EPCBarcodes, POSTs them to the
    /// EPC imports API in batches of 500, records each accepted batch in
    /// LPMSIM.dbo.EPCBarcodes_ApiCallDetails, and writes one
    /// dbo.WmsRptJobRun row. Never throws — the outcome is in the returned tuple and
    /// in the run log, so a caller does not need its own try/catch.
    /// </summary>
    public async Task<(int Rows, string? Error)> SendPendingEpcAsync(string mode, string triggeredBy, CancellationToken ct = default)
    {
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate work.", ct);
            return (0, null);
        }

        var runId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        try
        {
            List<EpcSourceRow> rows;
            await using (var c = OpenOnPremBackup())
            {
                var dbRows = await c.QueryAsync<EpcSourceRow>(new CommandDefinition(
                    SourceQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                rows = dbRows.AsList();
            }

            if (rows.Count == 0)
            {
                await jobs.FinishRunAsync(runId, "Success", 0, null, ct);
                return (0, null);
            }

            var sent = 0;
            foreach (var chunk in rows.Chunk(MaxItemsPerRequest))
            {
                var items = chunk.Select(r => new EpcApiItem(
                    Site:         r.Site ?? "",
                    SubLocation:  r.SubLocation ?? "",
                    Epc:          r.Epc ?? "",
                    Sku:          r.Sku ?? "",
                    LotNumber:    r.LotNumber ?? "",
                    Quantity:     r.Quantity,
                    Ean:          r.Ean ?? "",
                    Barcode:      r.Barcode ?? "",
                    SerialNumber: r.SerialNumber ?? "",
                    Function:     r.Function ?? "",
                    CreationDate: r.CreationDateUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                    Printer:      r.Printer ?? "",
                    AntiTheft:    r.AntiTheft)).ToList();

                using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                req.Headers.Add("apikey", opts.Value.ApiKey);
                req.Content = JsonContent.Create(new EpcApiRequest(items));

                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                {
                    var error = $"HTTP {(int)res.StatusCode}: {body} (sent {sent} of {rows.Count} before this failure)";
                    await jobs.FinishRunAsync(runId, "Failed", sent, error, ct);
                    return (sent, error);
                }

                await using (var c = OpenOnPremBackup())
                {
                    await c.ExecuteAsync(new CommandDefinition(
                        MarkSentSql, new { srnos = chunk.Select(r => r.Srno).ToList() },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                }

                sent += chunk.Length;
            }

            await jobs.FinishRunAsync(runId, "Success", sent, null, ct);
            return (sent, null);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", 0, ex.Message, ct);
            return (0, ex.Message);
        }
    }
}
