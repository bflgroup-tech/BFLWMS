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
    string? Site, string? SubLocation, string? Epc, string? Sku, string? LotNumber,
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
/// KNOWN GAP, pending confirmation before this should run unattended: no
/// "already sent" filter — EPCBarcodes was said to carry a status/sent column,
/// but its name hasn't been given yet, so the query pulls (and will re-POST)
/// every row in the table on every call. Wire the filter + a post-send UPDATE
/// once that column is confirmed. On-demand only ("Send Now" on Nightly
/// Batches) — no timer, specifically because of this gap.
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

    // WmsProductionDb, not OnPremBackup — same as GenerateEan13Service for
    // DATAREPORTING writes: the OnPremBackup login has been denied UPDATE
    // there before. Used here for the read too, so the eventual "mark sent"
    // UPDATE lands on the same connection/transaction semantics.
    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(resolver.GetWmsProductionDbConnectionString());
        c.Open();
        return c;
    }

    private const string SourceQuery = @"
        SELECT Site         = (SELECT StoreID FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopName),
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
          FROM DATAREPORTING.dbo.EPCBarcodes a";

    /// <summary>
    /// Reads eligible rows from DATAREPORTING.dbo.EPCBarcodes (see class doc for the
    /// known gaps), POSTs them to the EPC imports API in one batch, and writes one
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
            await using (var c = OpenWmsProductionDb())
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

            var items = rows.Select(r => new EpcApiItem(
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

            var sent = 0;
            foreach (var chunk in items.Chunk(MaxItemsPerRequest))
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                req.Headers.Add("apikey", opts.Value.ApiKey);
                req.Content = JsonContent.Create(new EpcApiRequest(chunk.ToList()));

                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);

                if (!res.IsSuccessStatusCode)
                {
                    var error = $"HTTP {(int)res.StatusCode}: {body} (sent {sent} of {items.Count} before this failure)";
                    await jobs.FinishRunAsync(runId, "Failed", sent, error, ct);
                    return (sent, error);
                }

                sent += chunk.Length;
            }

            // TODO: once the EPCBarcodes status/sent column is confirmed (see class
            // doc gap #1), mark these rows as sent here so a repeat run doesn't
            // re-POST them.
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
