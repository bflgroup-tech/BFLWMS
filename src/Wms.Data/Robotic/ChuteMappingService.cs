using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Wms.Core;
using Wms.Data.Configuration;

namespace Wms.Data.Robotic;

/// <summary>
/// Backing service for the Chute Mapping page — reads/writes the Robotics SQL
/// Server (ROBOTICS.dbo.ChuteConfiguration / ChuteIdMaster / ChuteConfigChangeLog,
/// BFLDATA.dbo.DataSettings) and calls the two external chute-mapping/status APIs.
/// Every method takes a warehouse code (e.g. "JAFZA", "TECHNO") since each
/// warehouse has its own Robotics DB connection and its own API endpoints/tokens.
/// </summary>
public class ChuteMappingService(
    HttpClient http,
    IOnPremConnectionResolver resolver,
    IOptions<RoboticApiOptions> apiOptions,
    ICurrentUser currentUser)
{
    private RoboticWarehouseOptions GetWarehouseConfig(string warehouse)
    {
        if (!apiOptions.Value.Warehouses.TryGetValue(warehouse, out var cfg))
            throw new InvalidOperationException($"No RoboticApi configuration found for warehouse '{warehouse}'.");
        return cfg;
    }

    private SqlConnection OpenRobo(string warehouse) =>
        new(resolver.GetRoboticsConnectionString(GetWarehouseConfig(warehouse).ConnectionStringKey));

    public async Task<Dictionary<string, int>> GetAllocatedChuteCountsAsync(string warehouse, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ChuteCountRow>(new CommandDefinition(
            @"SELECT CAST(ShopId AS VARCHAR) AS ShopId, COUNT(ChuteId) AS ChuteCount
              FROM ROBOTICS.dbo.ChuteConfiguration
              WHERE ShopId IS NOT NULL
              GROUP BY ShopId",
            commandTimeout: 15, cancellationToken: ct));
        return rows
            .Where(r => r.ShopId != null)
            .ToDictionary(r => r.ShopId!, r => r.ChuteCount);
    }

    public async Task<int> GetChutePendingQtyAsync(string warehouse, string chuteId, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var qty = await robo.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            @"SELECT ISNULL(SUM(qty), 0)
              FROM ROBOTICS.dbo.SortingConformationDetail
              WHERE ChuiteId = @ChuteId AND TransferNo = ''",
            new { ChuteId = chuteId }, commandTimeout: 15, cancellationToken: ct));
        return qty ?? 0;
    }

    public async Task<string> SaveChuteMappingAsync(string warehouse, string chuteId, string shopId, string shopName, CancellationToken ct = default)
    {
        var cfg  = GetWarehouseConfig(warehouse);
        var body = $"{{\"mapping\":[{{\"chute_id\":\"{chuteId}\",\"shop_id\":\"{shopId}\",\"shop_name\":\"{shopName}\"}}]}}";
        var req  = new HttpRequestMessage(HttpMethod.Post, cfg.ChuteMappingApiUrl)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(cfg.ChuteMappingApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ChuteMappingApiToken);
        req.Headers.Add("Accept", "application/json");
        var resp    = await http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"API error {(int)resp.StatusCode}: {content}");

        string apiMessage = "Saved successfully.";
        try
        {
            using var doc = JsonDocument.Parse(content);
            var msg = doc.RootElement.TryGetProperty("message", out var mp) ? mp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(msg))
                apiMessage = msg;
        }
        catch (JsonException) { }

        // Update local DB after API confirms success
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);

        // Verify the row exists before updating (diagnose WHERE-clause mismatch)
        var existing = await robo.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT ChuteId, ShopId, ShopName FROM ROBOTICS.dbo.ChuteConfiguration WHERE ChuteId = @ChuteId",
            new { ChuteId = chuteId });
        if (existing == null)
        {
            // Try a LIKE query to detect padding/encoding differences
            var similar = await robo.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT TOP 1 ChuteId, LEN(ChuteId) AS Len FROM ROBOTICS.dbo.ChuteConfiguration WHERE ChuteId LIKE @Pattern",
                new { Pattern = chuteId.Trim() + "%" });
            string hint = similar != null
                ? $" (found similar: '{similar.ChuteId}' len={similar.Len})"
                : " (no similar rows found)";
            throw new Exception($"Row not found in ChuteConfiguration: ChuteId='{chuteId}' len={chuteId.Length}{hint}");
        }

        await using var cmd = new SqlCommand(
            "UPDATE ROBOTICS.dbo.ChuteConfiguration SET ShopId = @ShopId, ShopName = @ShopName WHERE ChuteId = @ChuteId",
            robo) { CommandTimeout = 15 };
        object shopIdParam = int.TryParse(shopId, out var shopIdInt) ? shopIdInt : shopId;
        cmd.Parameters.AddWithValue("@ShopId", shopIdParam);
        cmd.Parameters.AddWithValue("@ShopName", shopName);
        cmd.Parameters.AddWithValue("@ChuteId", chuteId);
        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

        if (rowsAffected == 0)
            throw new Exception($"UPDATE ran but 0 rows affected for ChuteId='{chuteId}' (existing ShopId={existing.ShopId}).");

        await robo.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ROBOTICS.dbo.ChuteConfigChangeLog VALUES (@ChuteId, GETDATE(), @Username, @Remarks)",
            new { ChuteId = chuteId, Username = currentUser.Name, Remarks = $"Location changed from {existing.ShopName} (ShopId: {existing.ShopId}) to {shopName} (ShopId: {shopId})" },
            commandTimeout: 15, cancellationToken: ct));

        return $"{apiMessage} (DB: {rowsAffected} row updated, was ShopId={existing.ShopId})";
    }

    public async Task ToggleChuteStatusAsync(string warehouse, string chuteId, int currentStatus, CancellationToken ct = default)
    {
        var cfg    = GetWarehouseConfig(warehouse);
        var enable = currentStatus != 0;

        // Call external API
        var body = $"{{\"chuteId\":\"{chuteId}\",\"status\":{(enable ? "true" : "false")}}}";
        var req  = new HttpRequestMessage(HttpMethod.Post, cfg.ChuteStatusApiUrl)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(cfg.ChuteStatusApiToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ChuteStatusApiToken);
        var resp    = await http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);

        if ((int)resp.StatusCode != 200)
            throw new Exception($"API error {(int)resp.StatusCode}: {content}");

        // Parse response body if present: { "status": true/false, "message": "..." }.
        // Some warehouses' status APIs (e.g. Techno's) return 200 with an empty/non-JSON
        // body on success rather than this envelope — the 200 status alone confirms success then.
        bool bodyStatus = true;
        string? bodyMessage = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                bodyStatus  = !root.TryGetProperty("status", out var sProp) || sProp.GetBoolean();
                bodyMessage = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : null;
            }
            catch (JsonException) { /* non-JSON 200 body — treat as success */ }
        }

        if (!bodyStatus)
            throw new Exception(string.IsNullOrWhiteSpace(bodyMessage) ? "Operation rejected by API." : bodyMessage);

        // API confirmed success — update DB
        var newStatus = enable ? 0 : 2;
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        await robo.ExecuteAsync(new CommandDefinition(
            "UPDATE ROBOTICS.dbo.ChuteIdMaster SET Status = @Status WHERE ChuteId = @ChuteId",
            new { Status = newStatus, ChuteId = chuteId }, commandTimeout: 15, cancellationToken: ct));

        var remarks = enable ? "Chute enabled" : "Chute disabled";
        await robo.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ROBOTICS.dbo.ChuteConfigChangeLog VALUES (@ChuteId, GETDATE(), @Username, @Remarks)",
            new { ChuteId = chuteId, Username = currentUser.Name, Remarks = remarks },
            commandTimeout: 15, cancellationToken: ct));
    }

    public async Task<List<ChuteConfigRow>> GetChuteConfigAsync(string warehouse, int layer, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ChuteConfigRow>(new CommandDefinition(
            @"SELECT a.ChuteId, a.Status, a.direction, b.ShopId, b.ShopName, b.TotId
              FROM ROBOTICS.dbo.ChuteIdMaster a, ROBOTICS.dbo.ChuteConfiguration b
              WHERE a.ChuteId = b.ChuteId AND a.layer = @layer
              ORDER BY a.ChuteId",
            new { layer }, commandTimeout: 30, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<ShopNameRow>> GetShopNamesForContainerAsync(string warehouse, string contNo, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ShopNameRow>(new CommandDefinition(
            "EXEC bfldata.dbo.stp_LoadShopNames @ContNo",
            new { ContNo = contNo }, commandTimeout: 30, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<ShopNameRow>> GetAllShopNamesAsync(string warehouse, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ShopNameRow>(new CommandDefinition(
            "SELECT RoboShopId, ShopName FROM BFLDATA.dbo.DataSettings WHERE active='Y' ORDER BY ShopName",
            commandTimeout: 15, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<ShopNameRow>> SearchShopsAsync(string warehouse, string term, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ShopNameRow>(new CommandDefinition(
            @"SELECT RoboShopId, ShopName FROM BFLDATA.dbo.DataSettings
              WHERE active='Y'
                AND (CAST(RoboShopId AS VARCHAR) LIKE '%' + @term + '%'
                     OR ShopName LIKE '%' + @term + '%')
              ORDER BY ShopName",
            new { term }, commandTimeout: 15, cancellationToken: ct));
        return rows.ToList();
    }

    // --- Techno-specific reads ---
    // Techno's ChuteIdMaster has no usable "layer" column (always NULL — no Single/Double
    // concept) and encodes direction in the ChuteId itself ("UP-A-01" / "DN-A-01") rather
    // than in a "direction" column. It also has no BFLDATA.dbo.DataSettings / stp_LoadShopNames
    // to source a shop list from, so the shop list is derived from ChuteConfiguration instead.

    public async Task<List<ChuteConfigRow>> GetChuteConfigTechnoAsync(string warehouse, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var rows = await robo.QueryAsync<ChuteConfigRow>(new CommandDefinition(
            @"SELECT a.ChuteId, a.Status,
                     CASE WHEN a.ChuteId LIKE 'UP-%' THEN 'UP' ELSE 'DOWN' END AS Direction,
                     b.ShopId, b.ShopName, b.TotId
              FROM ROBOTICS.dbo.ChuteIdMaster a, ROBOTICS.dbo.ChuteConfiguration b
              WHERE a.ChuteId = b.ChuteId
              ORDER BY a.ChuteId",
            commandTimeout: 30, cancellationToken: ct));
        return rows.ToList();
    }

    // Shop list for the "Shop" / "Building - Division" dropdown modes, read from the requesting
    // warehouse's own BFLDATA.dbo.DataSettings ('N' = Shop, 'Y' = Building - Division — further
    // restricted to shops under the R1-DV division via ShopinShop). Per-shop chute counts still
    // come separately from that warehouse's ROBOTICS.dbo.ChuteConfiguration via GetAllocatedChuteCountsAsync.
    public async Task<List<ShopNameRow>> GetShopsByBuildingFlagAsync(string warehouse, string buildingFlag, CancellationToken ct = default)
    {
        await using var robo = OpenRobo(warehouse);
        await robo.OpenAsync(ct);
        var sql = buildingFlag == "Y"
            ? @"SELECT RoboShopId, ShopName FROM BFLDATA.dbo.DataSettings
                WHERE building='Y' AND active='Y'
                  AND ShopName IN (SELECT SubShop FROM BFLDATA.dbo.ShopinShop WHERE MainShop='R1-DV')"
            : "SELECT RoboShopId, ShopName FROM BFLDATA.dbo.DataSettings WHERE building=@Building AND active='Y'";
        var rows = await robo.QueryAsync<ShopNameRow>(new CommandDefinition(
            sql, new { Building = buildingFlag }, commandTimeout: 15, cancellationToken: ct));
        return rows.ToList();
    }
}
