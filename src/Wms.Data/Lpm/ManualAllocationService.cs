using System.Data;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the LPM Manual Allocation Upload page (menu key
/// MANUAL_ALLOCATION_UPLOAD). Sources:
///
///   Division   from Datareporting.dbo.vUpc_subclass + SubclassMaster
///   POQty      from usa.dbo.usaorgfile (aggregated per Contno + Itemcode)
///   eComSOH    from racks.dbo.LPM_locstock where storeid = @StoreID
///   SkuMax     from LPMSIM.dbo.LPM_SimItemSkuMax where StoreId = @StoreID
///   SkuBalance = SkuMax - eComSOH
///   Qualified  = MIN(SkuBalance, AllocationQty)
///   DivEOM     from LPMSIM.dbo.LPM_EOM_Output where StoreId = @StoreID
///              AND Month1 = current GST month AND Year1 = current GST year
///              (aggregated per DivCode)
///   DivSOH     from racks.dbo.LPM_locstock joined via vUpc_subclass -&gt;
///              SubclassMaster to get Division (aggregated per Division for
///              @StoreID)
///   EomBalance = DivEOM - DivSOH
///
/// All source reads go through the OnPremBackup connection (same server
/// hosts LPMSIM / usa / racks / Datareporting via 3-part names).
/// Persistence writes go to dbo.WmsManualAllocation on Azure WMS.
/// </summary>
public class ManualAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    private string Country =>
        user.Country
        ?? throw new InvalidOperationException(
            "Current user has no Country assigned — cannot run Manual Allocation Upload.");

    // ========== Country / store dropdowns ==========

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT SIMCountry
              FROM dbo.WMS_DataSettings WITH (NOLOCK)
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
             ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ManualAllocationStoreRow>> GetStoresForCountryAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return new();
        await using var c = OpenWms();
        var rows = await c.QueryAsync<ManualAllocationStoreRow>(new CommandDefinition(@"
            SELECT DISTINCT
                   StoreID,
                   StoreName = ISNULL(PBFullname, StoreID)
              FROM dbo.WMS_DataSettings WITH (NOLOCK)
             WHERE SIMCountry = @country
               AND StoreID IS NOT NULL AND LTRIM(RTRIM(StoreID)) <> ''
               AND (ActiveStore IS NULL OR ActiveStore <> 'N')
             ORDER BY StoreName",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ========== Container validation ==========

    /// <summary>Block if there's any scan row in WMSContBuildScanData for this
    /// ContNo — i.e., building has already started for this container. The
    /// operator would need to Clear those first before re-uploading a
    /// different allocation.</summary>
    public async Task<ManualAllocationValidateResult> ValidateContainerAsync(string contno, CancellationToken ct = default)
    {
        contno = (contno ?? "").Trim();
        if (string.IsNullOrEmpty(contno))
            return new(false, "Container number is required.");

        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMSContBuildScanData WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (hit == 1)
            return new(false,
                $"Container {contno} already has scan activity in WMSContBuildScanData — building has started. Clear those scans first before re-uploading.");
        return new(true, null);
    }

    // ========== Enrichment ==========

    public async Task<List<ManualAllocationRow>> EnrichRowsAsync(
        string contno, string storeId, string simCountry,
        List<ManualAllocationUploadRow> uploaded,
        CancellationToken ct = default)
    {
        contno     = (contno     ?? "").Trim();
        storeId    = (storeId    ?? "").Trim();
        simCountry = (simCountry ?? "").Trim();
        if (uploaded.Count == 0) return new();

        // Distinct itemcodes across all uploaded rows — used as IN @codes for the
        // per-item lookups below.
        var codes = uploaded.Select(r => r.Itemcode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var now   = DateTime.UtcNow.AddHours(4);  // GST
        var month = now.Month;
        var year  = now.Year;

        // Wave 1 — three item-level lookups run in parallel. Each opens its
        // own SqlConnection because Dapper + SqlConnection are not thread-safe
        // for concurrent commands.

        // 1) Division + DivCode per itemcode.
        async Task<Dictionary<string, (int? DivCode, string? Division)>> LoadDivByItem()
        {
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(string Itemcode, int? DivCode, string? Division)>(new CommandDefinition(
                @"SELECT itemcode, DivID AS DivCode, Division
                    FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
                   WHERE itemcode IN @codes",
                new { codes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .GroupBy(r => r.Itemcode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.First().DivCode, g.First().Division), StringComparer.OrdinalIgnoreCase);
        }

        // 2) POQty + GroupCode aggregated per itemcode for this ContNo.
        //    GroupCode is later joined to LPM_SKUMaxRule bands.
        async Task<Dictionary<string, (int POQty, string? GroupCode)>> LoadPoAndGroup()
        {
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(string Itemcode, int POQty, string? GroupCode)>(new CommandDefinition(
                @"SELECT ItemCode,
                         SUM(CAST(ISNULL(orgqty,0) AS INT)) AS POQty,
                         MAX(GroupCode)                     AS GroupCode
                    FROM usa.dbo.usaorgfile WITH (NOLOCK)
                   WHERE ContNo = @c AND ItemCode IN @codes
                   GROUP BY ItemCode",
                new { c = contno, codes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.Itemcode, r => (r.POQty, r.GroupCode), StringComparer.OrdinalIgnoreCase);
        }

        // 3) eCom SOH per itemcode at StoreID. Column is racks.LPM_locstock.SOH.
        async Task<Dictionary<string, int>> LoadSohByItem()
        {
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(string Itemcode, int SOH)>(new CommandDefinition(
                @"SELECT itemcode, SUM(CAST(ISNULL(SOH,0) AS INT)) AS SOH
                    FROM racks.dbo.LPM_locstock WITH (NOLOCK)
                   WHERE storeid = @s AND itemcode IN @codes
                   GROUP BY itemcode",
                new { s = storeId, codes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.Itemcode, r => r.SOH, StringComparer.OrdinalIgnoreCase);
        }

        var w1 = LoadDivByItem();
        var w2 = LoadPoAndGroup();
        var w3 = LoadSohByItem();
        await Task.WhenAll(w1, w2, w3);
        var divByItem  = await w1;
        var poAndGroup = await w2;
        var sohByItem  = await w3;

        // Restrict wave-2 aggregate queries to the DivCodes / Divisions that
        // actually appear in this upload — Q6 in particular used to join the
        // entire store's locstock through vupc_subclass with no division
        // filter, which was the dominant cost for large e-com uploads.
        var divCodes  = divByItem.Values.Select(v => v.DivCode ?? 0).Where(d => d != 0).Distinct().ToArray();
        var divisions = divByItem.Values.Select(v => v.Division ?? "").Where(d => !string.IsNullOrEmpty(d))
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Wave 2 — three aggregate lookups keyed off wave-1 dimensions,
        // in parallel on independent connections.

        // 4) SkuMax bands via LPMSIM.dbo.LPM_SKUMaxRule.
        async Task<List<(string Country, int DivCode, string? GroupCode, int WHStockFrom, int WHStockTo, int SKUMax)>> LoadSkuBands()
        {
            if (string.IsNullOrWhiteSpace(simCountry) || divCodes.Length == 0)
                return new();
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(string Country, int DivCode, string? GroupCode, int WHStockFrom, int WHStockTo, int SKUMax)>(new CommandDefinition(
                @"SELECT Country, DivCode, GroupCode, WHStockFrom, WHStockTo, SKUMax
                    FROM LPMSIM.dbo.LPM_SKUMaxRule WITH (NOLOCK)
                   WHERE Country = @country AND DivCode IN @divs AND IsActive = 1",
                new { country = simCountry, divs = divCodes },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        // 5) Div EOM aggregated per DivCode for this StoreID + current GST month/year.
        async Task<Dictionary<int, int>> LoadEomByDivCode()
        {
            if (divCodes.Length == 0) return new();
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(int DivCode, int TargetEOM)>(new CommandDefinition(
                @"SELECT DivCode, SUM(CAST(ISNULL(TargetEOM,0) AS INT)) AS TargetEOM
                    FROM LPMSIM.dbo.LPM_EOM_Output WITH (NOLOCK)
                   WHERE StoreId = @s AND Month1 = @m AND Year1 = @y AND DivCode IN @divs
                   GROUP BY DivCode",
                new { s = storeId, m = month, y = year, divs = divCodes },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.DivCode, r => r.TargetEOM);
        }

        // 6) Div SOH per Division text for this StoreID — restricted to the
        // divisions actually referenced by the upload.
        async Task<Dictionary<string, int>> LoadSohByDiv()
        {
            if (divisions.Length == 0)
                return new(StringComparer.OrdinalIgnoreCase);
            await using var c = OpenOnPremBackup();
            return (await c.QueryAsync<(string Division, int SOH)>(new CommandDefinition(
                @"SELECT v.Division, SUM(CAST(ISNULL(ls.SOH,0) AS INT)) AS SOH
                    FROM racks.dbo.LPM_locstock ls WITH (NOLOCK)
                    JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK) ON v.itemcode = ls.itemcode
                   WHERE ls.storeid = @s AND v.Division IN @divs
                   GROUP BY v.Division",
                new { s = storeId, divs = divisions },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Where(r => !string.IsNullOrEmpty(r.Division))
                .ToDictionary(r => r.Division!, r => r.SOH, StringComparer.OrdinalIgnoreCase);
        }

        var w4 = LoadSkuBands();
        var w5 = LoadEomByDivCode();
        var w6 = LoadSohByDiv();
        await Task.WhenAll(w4, w5, w6);
        var skuBands     = await w4;
        var eomByDivCode = await w5;
        var sohByDiv     = await w6;

        var skuBandsByKey = skuBands
            .GroupBy(b => (b.DivCode, GroupCode: (b.GroupCode ?? "").Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.WHStockFrom).ToList());

        int? SkuMaxFor(int divCode, string? groupCode, int poQty)
        {
            var gc = (groupCode ?? "").Trim().ToUpperInvariant();
            if (!skuBandsByKey.TryGetValue((divCode, gc), out var bands)) return null;
            foreach (var b in bands)
                if (poQty >= b.WHStockFrom && poQty <= b.WHStockTo) return b.SKUMax;
            return null;
        }

        // Merge everything row-by-row.
        var result = new List<ManualAllocationRow>(uploaded.Count);
        foreach (var u in uploaded)
        {
            divByItem.TryGetValue(u.Itemcode, out var div);
            var hasPo   = poAndGroup.TryGetValue(u.Itemcode, out var poEntry);
            var hasSoh  = sohByItem.TryGetValue(u.Itemcode, out var eSoh);

            int? skuMax = hasPo && div.DivCode.HasValue
                ? SkuMaxFor(div.DivCode.Value, poEntry.GroupCode, poEntry.POQty)
                : null;

            int? skuBal   = skuMax.HasValue ? skuMax.Value - eSoh : (int?)null;
            int? qualified = skuBal.HasValue ? Math.Max(0, Math.Min(skuBal.Value, u.AllocationQty)) : null;

            int? divEom   = div.DivCode.HasValue && eomByDivCode.TryGetValue(div.DivCode.Value, out var e) ? e : null;
            int? divSoh   = !string.IsNullOrEmpty(div.Division) && sohByDiv.TryGetValue(div.Division!, out var s) ? s : null;
            int? eomBal   = (divEom.HasValue && divSoh.HasValue) ? divEom - divSoh : null;

            result.Add(new ManualAllocationRow(
                StoreID:       u.StoreID,
                ContNo:        u.ContNo,
                Itemcode:      u.Itemcode,
                AllocationQty: u.AllocationQty,
                Division:      div.Division,
                POQty:         hasPo  ? poEntry.POQty : (int?)null,
                eComSOH:       hasSoh ? eSoh          : (int?)null,
                SkuMax:        skuMax,
                SkuBalance:    skuBal,
                QualifiedQty:  qualified,
                DivEOM:        divEom,
                DivSOH:        divSoh,
                EomBalance:    eomBal));
        }
        return result;
    }

    // ========== Save ==========

    public async Task<ManualAllocationSaveResult> SaveAsync(string contno, List<ManualAllocationRow> rows, CancellationToken ct = default)
    {
        contno = (contno ?? "").Trim();
        if (string.IsNullOrEmpty(contno)) return new(false, "Container number is required.", 0);
        if (rows.Count == 0)              return new(false, "No rows to save.", 0);
        var country = Country;

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.WmsManualAllocation WHERE Country = @ct AND ContNo = @c",
                new { ct = country, c = contno }, transaction: tx, cancellationToken: ct));

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Country",       typeof(string));
            dt.Columns.Add("StoreID",       typeof(string));
            dt.Columns.Add("ContNo",        typeof(string));
            dt.Columns.Add("Itemcode",      typeof(string));
            dt.Columns.Add("AllocationQty", typeof(int));
            dt.Columns.Add("Division",      typeof(string));
            dt.Columns.Add("POQty",         typeof(int));
            dt.Columns.Add("eComSOH",       typeof(int));
            dt.Columns.Add("SkuMax",        typeof(int));
            dt.Columns.Add("SkuBalance",    typeof(int));
            dt.Columns.Add("QualifiedQty",  typeof(int));
            dt.Columns.Add("DivEOM",        typeof(int));
            dt.Columns.Add("DivSOH",        typeof(int));
            dt.Columns.Add("EomBalance",    typeof(int));
            dt.Columns.Add("UploadedBy",    typeof(string));

            foreach (var r in rows)
            {
                dt.Rows.Add(
                    country,
                    r.StoreID,
                    r.ContNo,
                    r.Itemcode,
                    r.AllocationQty,
                    (object?)r.Division      ?? DBNull.Value,
                    (object?)r.POQty         ?? DBNull.Value,
                    (object?)r.eComSOH       ?? DBNull.Value,
                    (object?)r.SkuMax        ?? DBNull.Value,
                    (object?)r.SkuBalance    ?? DBNull.Value,
                    (object?)r.QualifiedQty  ?? DBNull.Value,
                    (object?)r.DivEOM        ?? DBNull.Value,
                    (object?)r.DivSOH        ?? DBNull.Value,
                    (object?)r.EomBalance    ?? DBNull.Value,
                    user.Name);
            }

            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.WmsManualAllocation",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);

            await tx.CommitAsync(ct);
            return new(true, null, rows.Count);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            return new(false, $"Save failed: {ex.Message}", 0);
        }
    }
}
