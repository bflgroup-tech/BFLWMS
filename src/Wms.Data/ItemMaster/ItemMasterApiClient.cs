using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wms.Data.ItemMaster;

public record ItemMasterItem(
    string  Upc,
    string  Itemcode,
    string? Itemname,
    string? Style,
    string? Brand,
    string? Size,
    string? Color,
    string? Groupcode,
    decimal? Rrp,
    string? Rrpcurrency,
    string? Gender,
    string? Season,
    string? Subcategory,
    string? Pricethreshold,
    string? Photourl,
    string? Department,
    string? Division,
    string? Groupname);

/// <summary>
/// External WMS Itemmaster API client. Handles login + token caching + one-shot
/// re-login retry on 401. Registered as a singleton so the token is shared
/// across requests within the app instance.
/// </summary>
public sealed class ItemMasterApiClient
{
    private readonly HttpClient _http;
    private readonly IOptions<ItemMasterApiOptions> _opts;
    private readonly ILogger<ItemMasterApiClient> _log;

    // Token cache — protected by _lock. Store the fully-formed
    // Authorization header value ("Bearer …") to match the shape the API
    // returns on login.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string?  _authHeader;
    private DateTime _authExpiresAt = DateTime.MinValue;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public ItemMasterApiClient(HttpClient http, IOptions<ItemMasterApiOptions> opts, ILogger<ItemMasterApiClient> log)
    {
        _http = http;
        _opts = opts;
        _log  = log;
    }

    /// <summary>Look up an item by UPC. Returns null when the API is not
    /// configured, the login fails, the item isn't found, or the call errors —
    /// callers should treat null as "not found" and fall back to the next source.
    /// A single 401 response triggers a token refresh + one retry.</summary>
    public async Task<ItemMasterItem?> GetByUpcAsync(string upc, CancellationToken ct = default)
    {
        var opts = _opts.Value;
        if (!opts.IsConfigured)
        {
            // Explicit skip log so ops can see the API tier wasn't consulted at all.
            _log.LogInformation("WMS Itemmaster API skipped for UPC {Upc} — client not configured (missing BaseUrl / Username / Password).", upc);
            return null;
        }
        if (string.IsNullOrWhiteSpace(upc)) return null;

        try
        {
            var res = await SendUpcRequestAsync(opts, upc, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Force a fresh login and retry once.
                await using var _ = new NoOp();  // (placeholder to keep pattern clear)
                _authHeader = null;
                _authExpiresAt = DateTime.MinValue;
                res.Dispose();
                res = await SendUpcRequestAsync(opts, upc, ct);
            }

            if (!res.IsSuccessStatusCode)
            {
                // Grab a body snippet so ops can see WHY the API rejected the call.
                string bodySnippet = "";
                try
                {
                    var body = await res.Content.ReadAsStringAsync(ct);
                    bodySnippet = body.Length > 500 ? body[..500] + "…" : body;
                }
                catch { /* ignore body read errors */ }
                _log.LogWarning("WMS Itemmaster API returned {Status} for UPC {Upc}. Body: {Body}",
                    (int)res.StatusCode, upc, bodySnippet);
                return null;
            }

            var payload = await res.Content.ReadFromJsonAsync<UpcResponse>(_json, ct);
            if (payload?.Data is null || payload.Status != true)
            {
                _log.LogInformation("WMS Itemmaster API returned 200 but no data for UPC {Upc} (status={Status}, message={Msg}).",
                    upc, payload?.Status, payload?.Message);
                return null;
            }

            var d = payload.Data;
            return new ItemMasterItem(
                Upc:            d.Upc            ?? upc,
                Itemcode:       d.Itemcode       ?? d.Upc ?? upc,
                Itemname:       d.Itemname,
                Style:          d.Style,
                Brand:          d.Brand,
                Size:           d.Size,
                Color:          d.Color,
                Groupcode:      d.Groupcode,
                Rrp:            d.Rrp,
                Rrpcurrency:    d.Rrpcurrency,
                Gender:         d.Gender,
                Season:         d.Season,
                Subcategory:    d.Subcategory,
                Pricethreshold: d.Pricethreshold,
                Photourl:       d.Photourl,
                Department:     d.Department,
                Division:       d.Division,
                Groupname:      d.Groupname);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WMS Itemmaster API UPC lookup failed for {Upc}", upc);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendUpcRequestAsync(ItemMasterApiOptions opts, string upc, CancellationToken ct)
    {
        var auth = await GetAuthHeaderAsync(opts, ct);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{opts.BaseUrl.TrimEnd('/')}/item/upc/{Uri.EscapeDataString(upc)}");
        if (!string.IsNullOrEmpty(auth))
            req.Headers.Add("Authorization", auth);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<string?> GetAuthHeaderAsync(ItemMasterApiOptions opts, CancellationToken ct)
    {
        // Fast path — token still valid.
        if (!string.IsNullOrEmpty(_authHeader) && DateTime.UtcNow < _authExpiresAt)
            return _authHeader;

        await _lock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock in case another caller just refreshed.
            if (!string.IsNullOrEmpty(_authHeader) && DateTime.UtcNow < _authExpiresAt)
                return _authHeader;

            var loginUri = $"{opts.BaseUrl.TrimEnd('/')}/Authentication/login";
            var body = new LoginRequest { Username = opts.Username, Password = opts.Password };
            using var loginReq = new HttpRequestMessage(HttpMethod.Post, loginUri);
            loginReq.Content = JsonContent.Create(body, options: _json);
            using var loginRes = await _http.SendAsync(loginReq, ct);
            if (!loginRes.IsSuccessStatusCode)
            {
                _log.LogWarning("WMS Itemmaster API login failed with status {Status}", loginRes.StatusCode);
                _authHeader = null;
                _authExpiresAt = DateTime.MinValue;
                return null;
            }

            var payload = await loginRes.Content.ReadFromJsonAsync<LoginResponse>(_json, ct);
            var token = payload?.Token;
            if (string.IsNullOrEmpty(token))
            {
                _log.LogWarning("WMS Itemmaster API login returned no token.");
                _authHeader = null;
                _authExpiresAt = DateTime.MinValue;
                return null;
            }

            // The API's Login response already includes the "Bearer " prefix on
            // some deployments and not others — normalize.
            _authHeader = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? token
                : "Bearer " + token;
            _authExpiresAt = DateTime.UtcNow + opts.TokenTtl;
            return _authHeader;
        }
        finally { _lock.Release(); }
    }

    // ----- Wire types -----

    private sealed class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    private sealed class LoginResponse
    {
        [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
        [JsonPropertyName("token")]     public string? Token { get; set; }
        [JsonPropertyName("authorization")] public string? Authorization { get; set; }
        [JsonPropertyName("message")]   public string? Message { get; set; }
    }

    private sealed class UpcResponse
    {
        [JsonPropertyName("status")]  public bool? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public UpcData? Data { get; set; }
    }

    private sealed class UpcData
    {
        public string?  Upc { get; set; }
        public string?  Itemcode { get; set; }
        public string?  Itemname { get; set; }
        public string?  Style { get; set; }
        public string?  Brand { get; set; }
        public string?  Size { get; set; }
        public string?  Color { get; set; }
        public string?  Groupcode { get; set; }
        public decimal? Rrp { get; set; }
        public string?  Rrpcurrency { get; set; }
        public string?  Gender { get; set; }
        public string?  Season { get; set; }
        public string?  Subcategory { get; set; }
        public string?  Pricethreshold { get; set; }
        public string?  Photourl { get; set; }
        public string?  Department { get; set; }
        public string?  Division { get; set; }
        public string?  Groupname { get; set; }
    }

    // Tiny helper so the placeholder above compiles cleanly.
    private sealed class NoOp : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
