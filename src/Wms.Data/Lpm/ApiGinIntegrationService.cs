using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Pushes GIN-linked shop-issue transfers to two external Altavant APIs, both keyed
/// off the same queue table (LPMSIM.dbo.APICallGIN) and the same GIN/shop-issue
/// scoped source query pattern (BFLDATA.dbo.vGoodsIssuePlt joined to
/// DATA2004.dbo.TransferDetail, serialized divisions only):
///
///   - Products (POST /v1/products) — one call per GIN with its distinct SKUs
///     enriched from DATAREPORTING/usa/HODATA (EAN, category, brand, price, etc.).
///     Tracked via ItemApiUpdate/ItemApiResponse.
///   - GINs (POST /v1/store-inbounds) — one call per GIN with its line items
///     (quantities from TransferDetail). Tracked via ApiUpdate/ApiResponse.
///
/// SendPendingAsync is the single entry point ("Send Now" on the Nightly Batches
/// admin page — no timer yet): one dbo.WmsRptJobRun row covering both, products
/// always run first because the receiving system needs a SKU's product master
/// record before an inbound referencing that SKU makes sense on its end.
///
/// Enqueuing new GINs into APICallGIN is not this service's job — it's handled
/// elsewhere (or manually); this service only sends what's already queued.
///
/// Both halves follow the same convention: the raw response body always gets saved
/// (success or not), and the *Update column is only stamped GETDATE() when the
/// response's "status" is "accepted" — anything else leaves it NULL so the next run
/// retries that GIN, while still recording what the API said. One GIN's failure
/// doesn't affect the others in the same run.
/// </summary>
public class ApiGinIntegrationService(
    IOnPremConnectionResolver resolver,
    ScheduledJobService jobs,
    HttpClient http,
    IOptions<ApiGinIntegrationOptions> apiOpts,
    ILogger<ApiGinIntegrationService> log)
{
    private const int CommandTimeoutSeconds = 60;
    public const string JobName = "APIGinIntegration";

    // WmsProductionDb, not OnPremBackup — same as GinTrailerUpdateService/
    // GenerateEan13Service/JafzaExportCheckingService/ContainerAllocationDataSyncService/
    // TechnoBuildingService for BFLDATA writes: the OnPremBackup login has been denied
    // UPDATE/INSERT there before. Every query here fully-qualifies its database
    // (LPMSIM.dbo./BFLDATA.dbo./DATA2004.dbo.) rather than relying on a default
    // catalog, so one connection reaches all three.
    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(resolver.GetWmsProductionDbConnectionString());
        c.Open();
        return c;
    }

    private const string PendingGinsSql = @"
        SELECT ginno AS GinNo, shopname AS ShopName
          FROM LPMSIM.dbo.APICallGIN
         WHERE ApiUpdate IS NULL;";

    private const string GinItemsSql = @"
        SELECT
            InboundReference      = a.TrfNo,
            SourceReference       = CAST(a.SrNo AS VARCHAR(50)),
            DestinationLocationId = (SELECT CAST(RMSStoreID AS VARCHAR(50)) FROM BFLDATA.dbo.DataSettings WHERE ShopName = a.ShopIssue),
            Sku                   = b.ItemCode,
            ExpectedQuantity      = SUM(b.Quantity),
            Parcel                = a.TrfNo,
            DeliveryDate          = a.DelDate
          FROM BFLDATA.dbo.vGoodsIssuePlt a
          JOIN DATA2004.dbo.TransferDetail b ON a.TrfNo = b.TrfNo
         WHERE a.SrNo = @ginNo AND a.ShopIssue = @shopName AND b.SerializedCode <> ''
         GROUP BY a.TrfNo, a.SrNo, a.ShopIssue, b.ItemCode, a.DelDate;";

    private const string SaveResponseSql = @"
        UPDATE LPMSIM.dbo.APICallGIN
           SET ApiResponse = @response
         WHERE ginno = @ginNo AND shopname = @shopName;";

    private const string MarkSentSql = @"
        UPDATE LPMSIM.dbo.APICallGIN
           SET ApiUpdate = GETDATE()
         WHERE ginno = @ginNo AND shopname = @shopName;";

    private sealed record PendingGin(string GinNo, string ShopName);

    // One entry per GIN/endpoint attempt, success or failure — serialized to JSON and
    // stored as the run's ErrorMessage so the Nightly Batches admin page can expand a
    // run row and show exactly which calls succeeded and which didn't, not just an
    // aggregate count.
    private sealed record GinCallResult(string Endpoint, string GinNo, string ShopName, bool Success, string? Message);

    // A settable-property class rather than a positional record: Dapper's fast
    // constructor-matching path for records needs the constructor parameter types to
    // exactly match the reader's column types (e.g. SrNo came back as int, DelDate as
    // a non-nullable datetime) and throws if they don't. Property-setter binding is
    // far more forgiving about numeric/nullability differences between the DB schema
    // and this shape.
    private sealed class GinItemRow
    {
        public string InboundReference { get; set; } = "";
        public string SourceReference { get; set; } = "";
        public string DestinationLocationId { get; set; } = "";
        public string Sku { get; set; } = "";
        public decimal ExpectedQuantity { get; set; }
        public string Parcel { get; set; } = "";
        public DateTime? DeliveryDate { get; set; }
    }

    // Core GIN-send loop over an already-open connection — no locking/run-log of its
    // own, so SendPendingAsync can run it as one step of a single combined job run.
    private async Task<(int Sent, int Failed, List<GinCallResult> Results)> SendGinsCoreAsync(
        SqlConnection c, ApiGinIntegrationOptions opts, CancellationToken ct)
    {
        var pending = (await c.QueryAsync<PendingGin>(new CommandDefinition(
            PendingGinsSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var sent = 0;
        var failed = 0;
        var results = new List<GinCallResult>();

        foreach (var gin in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var items = (await c.QueryAsync<GinItemRow>(new CommandDefinition(
                    GinItemsSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

                if (items.Count == 0)
                {
                    // Nothing serialized/found for this GIN — mark it sent anyway so
                    // it doesn't block every future run retrying a GIN with no lines.
                    // No API call was made, so ApiResponse is left as-is.
                    await c.ExecuteAsync(new CommandDefinition(
                        MarkSentSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    results.Add(new GinCallResult("gins", gin.GinNo, gin.ShopName, true, "No serialized lines — marked sent, no API call."));
                    sent++;
                    continue;
                }

                var request = new GinApiRequest
                {
                    Items = items.Select(i => new GinApiItem
                    {
                        InboundReference      = i.InboundReference,
                        InboundType            = "WAREHOUSE_TRANSFER",
                        SourceReference        = i.SourceReference,
                        LineReference          = "",
                        SourceLocationId       = "",
                        DestinationLocationId  = i.DestinationLocationId ?? "",
                        SupplierId             = "",
                        Sku                    = i.Sku,
                        ExpectedQuantity       = i.ExpectedQuantity,
                        Uom                    = "EA",
                        Pallet                 = "",
                        Parcel                 = i.Parcel,
                        TrackingNumber         = "",
                        DeliveryDate           = i.DeliveryDate?.ToString("yyyy-MM-dd") ?? "",
                    }).ToList(),
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, opts.BaseUrl);
                req.Headers.Add("apikey", opts.ApiKey);
                req.Content = JsonContent.Create(request);

                using var res = await http.SendAsync(req, ct);
                var body = await SafeReadBodyAsync(res, ct);

                // Always record what the API said, success or not.
                await c.ExecuteAsync(new CommandDefinition(
                    SaveResponseSql, new { ginNo = gin.GinNo, shopName = gin.ShopName, response = body },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                GinApiResponse? parsed = null;
                try { parsed = System.Text.Json.JsonSerializer.Deserialize<GinApiResponse>(body); }
                catch { /* non-JSON or unexpected body — accepted check below just fails closed */ }

                var accepted = res.IsSuccessStatusCode
                    && string.Equals(parsed?.Status, "accepted", StringComparison.OrdinalIgnoreCase);

                if (!accepted)
                {
                    failed++;
                    var reason = res.IsSuccessStatusCode ? $"status={parsed?.Status}" : $"HTTP {(int)res.StatusCode}";
                    results.Add(new GinCallResult("gins", gin.GinNo, gin.ShopName, false, $"{reason} — {Truncate(body)}"));
                    log.LogWarning("APIGinIntegration: GIN {Gin}/{Shop} not accepted ({Reason}). Body: {Body}",
                        gin.GinNo, gin.ShopName, reason, Truncate(body));
                    continue;
                }

                await c.ExecuteAsync(new CommandDefinition(
                    MarkSentSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                results.Add(new GinCallResult("gins", gin.GinNo, gin.ShopName, true, null));
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new GinCallResult("gins", gin.GinNo, gin.ShopName, false, ex.Message));
                log.LogWarning(ex, "APIGinIntegration: GIN {Gin}/{Shop} threw.", gin.GinNo, gin.ShopName);
            }
        }

        return (sent, failed, results);
    }

    private const string PendingProductsSql = @"
        SELECT ginno AS GinNo, shopname AS ShopName
          FROM LPMSIM.dbo.APICallGIN
         WHERE ItemApiUpdate IS NULL;";

    // Builds #items for one GIN's shop-issue transfer: distinct SKUs, then enriched
    // from four separate source systems (DATAREPORTING for EAN/category/name, usa for
    // product_id/brand/season/color/size/description, usa.upcAddress for image_url,
    // HODATA for selling_price), plus fixed defaults for the fields those sources
    // don't carry. Same cross-database, single-connection pattern as GinItemsSql —
    // every table is fully qualified, no reliance on a default catalog.
    private const string ProductItemsSql = @"
        DROP TABLE IF EXISTS #items;

        SELECT DISTINCT sku = ItemCode
          INTO #items
          FROM BFLDATA.dbo.vGoodsIssuePlt a, DATA2004.dbo.TransferDetail b
         WHERE a.SrNo = @ginNo AND a.ShopIssue = @shopName AND a.TrfNo = b.TrfNo AND b.SerializedCode <> '';

        ALTER TABLE #items ADD
            ean varchar(25), product_id varchar(150), category_id varchar(15), name varchar(150),
            description varchar(150), selling_price float, buying_price float, image_url varchar(150),
            unit varchar(10), brand varchar(150), season varchar(15), color varchar(150), size varchar(150),
            supplier varchar(150), main_rfid_tag varchar(15), other_rfid_tag varchar(15), customfield_1 varchar(150);

        UPDATE #items SET ean = b.ean13, category_id = b.classid, name = b.class
          FROM #items a, DATAREPORTING.dbo.vUPC_SUBCLASS b WHERE a.sku = b.itemcode;

        UPDATE #items SET product_id = b.Style, brand = b.Vendor, season = b.ItemType, color = b.Color, size = b.Size1, description = b.itemname
          FROM #items a, usa.dbo.UPCBarCodes b WHERE a.sku = b.itemcode;

        UPDATE #items SET image_url = b.FileLoc
          FROM #items a, usa.dbo.upcAddress b WHERE a.sku = b.upc;

        UPDATE #items SET unit = '001', main_rfid_tag = 'EPC', other_rfid_tag = 'EAN', customfield_1 = '', buying_price = 0, supplier = '';

        UPDATE #items SET selling_price = b.SalesRate
          FROM #items a, HODATA.dbo.SalesPrice b WHERE a.sku = b.itemcode;

        UPDATE #items SET image_url = '' WHERE image_url IS NULL;

        SELECT
            Sku = sku, Ean = ean, ProductId = product_id, CategoryId = category_id, Name = name,
            Description = description, SellingPrice = selling_price, BuyingPrice = buying_price,
            ImageUrl = image_url, Unit = unit, Brand = brand, Season = season, Color = color, Size = size,
            Supplier = supplier, MainRfidTag = main_rfid_tag, OtherRfidTag = other_rfid_tag, CustomField1 = customfield_1
          FROM #items;";

    private const string SaveItemResponseSql = @"
        UPDATE LPMSIM.dbo.APICallGIN
           SET ItemApiResponse = @response
         WHERE ginno = @ginNo AND shopname = @shopName;";

    private const string MarkItemSentSql = @"
        UPDATE LPMSIM.dbo.APICallGIN
           SET ItemApiUpdate = GETDATE()
         WHERE ginno = @ginNo AND shopname = @shopName;";

    // Settable-property class, same Dapper-tolerance reasoning as GinItemRow.
    private sealed class ProductItemRow
    {
        public string Sku { get; set; } = "";
        public string? Ean { get; set; }
        public string? ProductId { get; set; }
        public string? CategoryId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public double? SellingPrice { get; set; }
        public double? BuyingPrice { get; set; }
        public string? ImageUrl { get; set; }
        public string? Unit { get; set; }
        public string? Brand { get; set; }
        public string? Season { get; set; }
        public string? Color { get; set; }
        public string? Size { get; set; }
        public string? Supplier { get; set; }
        public string? MainRfidTag { get; set; }
        public string? OtherRfidTag { get; set; }
        public string? CustomField1 { get; set; }
    }

    // Core product-send loop, same shape as SendGinsCoreAsync — no locking/run-log of
    // its own.
    private async Task<(int Sent, int Failed, List<GinCallResult> Results)> SendProductsCoreAsync(
        SqlConnection c, ApiGinIntegrationOptions opts, CancellationToken ct)
    {
        var pending = (await c.QueryAsync<PendingGin>(new CommandDefinition(
            PendingProductsSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var sent = 0;
        var failed = 0;
        var results = new List<GinCallResult>();

        foreach (var gin in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var items = (await c.QueryAsync<ProductItemRow>(new CommandDefinition(
                    ProductItemsSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

                if (items.Count == 0)
                {
                    await c.ExecuteAsync(new CommandDefinition(
                        MarkItemSentSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    results.Add(new GinCallResult("products", gin.GinNo, gin.ShopName, true, "No SKUs found — marked sent, no API call."));
                    sent++;
                    continue;
                }

                var request = new ProductApiRequest
                {
                    Items = items.Select(i => new ProductApiItem
                    {
                        Sku            = i.Sku,
                        Ean            = i.Ean ?? "",
                        ProductId      = i.ProductId ?? "",
                        CategoryId     = i.CategoryId ?? "",
                        Name           = i.Name ?? "",
                        Description    = i.Description ?? "",
                        SellingPrice   = i.SellingPrice ?? 0,
                        BuyingPrice    = i.BuyingPrice ?? 0,
                        ImageUrl       = i.ImageUrl ?? "",
                        Unit           = i.Unit ?? "",
                        Brand          = i.Brand ?? "",
                        Season         = i.Season ?? "",
                        Color          = i.Color ?? "",
                        Size           = i.Size ?? "",
                        Supplier       = i.Supplier ?? "",
                        MainRfidTag    = i.MainRfidTag ?? "",
                        OtherRfidTag   = i.OtherRfidTag ?? "",
                        CustomField1   = i.CustomField1 ?? "",
                    }).ToList(),
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, opts.ProductsUrl);
                req.Headers.Add("apikey", opts.ApiKey);
                req.Content = JsonContent.Create(request);

                using var res = await http.SendAsync(req, ct);
                var body = await SafeReadBodyAsync(res, ct);

                await c.ExecuteAsync(new CommandDefinition(
                    SaveItemResponseSql, new { ginNo = gin.GinNo, shopName = gin.ShopName, response = body },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                GinApiResponse? parsed = null;
                try { parsed = System.Text.Json.JsonSerializer.Deserialize<GinApiResponse>(body); }
                catch { /* non-JSON or unexpected body — accepted check below just fails closed */ }

                var accepted = res.IsSuccessStatusCode
                    && string.Equals(parsed?.Status, "accepted", StringComparison.OrdinalIgnoreCase);

                if (!accepted)
                {
                    failed++;
                    var reason = res.IsSuccessStatusCode ? $"status={parsed?.Status}" : $"HTTP {(int)res.StatusCode}";
                    results.Add(new GinCallResult("products", gin.GinNo, gin.ShopName, false, $"{reason} — {Truncate(body)}"));
                    log.LogWarning("APIGinIntegration (products): GIN {Gin}/{Shop} not accepted ({Reason}). Body: {Body}",
                        gin.GinNo, gin.ShopName, reason, Truncate(body));
                    continue;
                }

                await c.ExecuteAsync(new CommandDefinition(
                    MarkItemSentSql, new { ginNo = gin.GinNo, shopName = gin.ShopName },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                results.Add(new GinCallResult("products", gin.GinNo, gin.ShopName, true, null));
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new GinCallResult("products", gin.GinNo, gin.ShopName, false, ex.Message));
                log.LogWarning(ex, "APIGinIntegration (products): GIN {Gin}/{Shop} threw.", gin.GinNo, gin.ShopName);
            }
        }

        return (sent, failed, results);
    }

    /// <summary>
    /// Single entry point for "Send Now" — runs products first, then GINs, in one
    /// dbo.WmsRptJobRun row. Products go first because the receiving system needs a
    /// SKU's product master record before an inbound referencing that SKU makes
    /// sense on its end. Never throws.
    /// </summary>
    public async Task<(int Sent, int Failed, string? Error)> SendPendingAsync(string mode, string triggeredBy, CancellationToken ct = default)
    {
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate work.", ct);
            return (0, 0, null);
        }

        var runId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        var opts = apiOpts.Value;
        if (!opts.IsConfigured)
        {
            const string notConfigured = "ApiGinIntegration:ApiKey is not configured.";
            await jobs.FinishRunAsync(runId, "Failed", null, notConfigured, ct);
            return (0, 0, notConfigured);
        }

        try
        {
            await using var c = OpenWmsProductionDb();

            var (productsSent, productsFailed, productResults) = await SendProductsCoreAsync(c, opts, ct);
            var (ginsSent, ginsFailed, ginResults) = await SendGinsCoreAsync(c, opts, ct);

            var sent = productsSent + ginsSent;
            var failed = productsFailed + ginsFailed;
            var results = productResults.Concat(ginResults).ToList();

            // Stored as ErrorMessage (the only free-text column on WmsRptJobRun) so
            // Recent Runs can expand this row and show every GIN/endpoint call, not
            // just an aggregate count — see NightlyBatches.razor's ParseCallResults.
            var detail = results.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(results);
            await jobs.FinishRunAsync(runId, failed == 0 ? "Success" : "Failed", sent, detail, ct);

            var errorSummary = failed == 0
                ? null
                : string.Join(" | ", results.Where(r => !r.Success).Take(5).Select(r => $"GIN {r.GinNo}/{r.ShopName} ({r.Endpoint}): {r.Message}"));
            return (sent, failed, errorSummary);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            return (0, 0, ex.Message);
        }
    }

    // Full, untruncated body — this is what gets saved to ApiResponse.
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    // Short form for the run log / warning log, which shouldn't carry a whole payload.
    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;

    // ----- Wire types (https://api.bfl.altavantconsulting.eu/v1/store-inbounds) -----

    private sealed class GinApiRequest
    {
        [JsonPropertyName("items")] public List<GinApiItem> Items { get; set; } = [];
    }

    private sealed class GinApiResponse
    {
        [JsonPropertyName("batch_id")]     public string? BatchId { get; set; }
        [JsonPropertyName("status")]       public string? Status { get; set; }
        [JsonPropertyName("record_count")] public int? RecordCount { get; set; }
    }

    private sealed class GinApiItem
    {
        [JsonPropertyName("inbound_reference")]       public string InboundReference { get; set; } = "";
        [JsonPropertyName("inbound_type")]             public string InboundType { get; set; } = "";
        [JsonPropertyName("source_reference")]         public string SourceReference { get; set; } = "";
        [JsonPropertyName("line_reference")]           public string LineReference { get; set; } = "";
        [JsonPropertyName("source_location_id")]       public string SourceLocationId { get; set; } = "";
        [JsonPropertyName("destination_location_id")]  public string DestinationLocationId { get; set; } = "";
        [JsonPropertyName("supplier_id")]              public string SupplierId { get; set; } = "";
        [JsonPropertyName("sku")]                      public string Sku { get; set; } = "";
        [JsonPropertyName("expected_quantity")]        public decimal ExpectedQuantity { get; set; }
        [JsonPropertyName("uom")]                      public string Uom { get; set; } = "";
        [JsonPropertyName("pallet")]                   public string Pallet { get; set; } = "";
        [JsonPropertyName("parcel")]                   public string Parcel { get; set; } = "";
        [JsonPropertyName("tracking_number")]          public string TrackingNumber { get; set; } = "";
        [JsonPropertyName("delivery_date")]            public string DeliveryDate { get; set; } = "";
    }

    // ----- Wire types (https://api.bfl.altavantconsulting.eu/v1/products) -----
    // Response shape is identical to the GIN endpoint's — reuses GinApiResponse.

    private sealed class ProductApiRequest
    {
        [JsonPropertyName("items")] public List<ProductApiItem> Items { get; set; } = [];
    }

    private sealed class ProductApiItem
    {
        [JsonPropertyName("sku")]            public string Sku { get; set; } = "";
        [JsonPropertyName("ean")]             public string Ean { get; set; } = "";
        [JsonPropertyName("product_id")]      public string ProductId { get; set; } = "";
        [JsonPropertyName("category_id")]     public string CategoryId { get; set; } = "";
        [JsonPropertyName("name")]            public string Name { get; set; } = "";
        [JsonPropertyName("description")]     public string Description { get; set; } = "";
        [JsonPropertyName("selling_price")]   public double SellingPrice { get; set; }
        [JsonPropertyName("buying_price")]    public double BuyingPrice { get; set; }
        [JsonPropertyName("image_url")]       public string ImageUrl { get; set; } = "";
        [JsonPropertyName("unit")]            public string Unit { get; set; } = "";
        [JsonPropertyName("brand")]           public string Brand { get; set; } = "";
        [JsonPropertyName("season")]          public string Season { get; set; } = "";
        [JsonPropertyName("color")]           public string Color { get; set; } = "";
        [JsonPropertyName("size")]            public string Size { get; set; } = "";
        [JsonPropertyName("supplier")]        public string Supplier { get; set; } = "";
        [JsonPropertyName("main_rfid_tag")]   public string MainRfidTag { get; set; } = "";
        [JsonPropertyName("other_rfid_tag")]  public string OtherRfidTag { get; set; } = "";
        [JsonPropertyName("customfield_1")]   public string CustomField1 { get; set; } = "";
    }
}
