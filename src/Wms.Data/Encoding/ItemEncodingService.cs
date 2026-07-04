using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Encoding;

public record OpenContnoRow(string ContNo, string? ContDesc);
public record PONoRow(string PONo);
public record BrandRow(string Brand);
public record StyleRow(string Style);
public record GenderRow(string Gender);
public record ColorRow(string Color);
public record SizeRow(string Size);

/// <summary>Whether a barcode/itemcode already exists on Azure — checked
/// against both WMS_ContAllocationData and WMS_Generatebarcode.</summary>
public record ItemcodeExistsResult(bool InAllocation, bool InGenerated);

/// <summary>One row of the Item Encoding page's Recent Activity grid — a
/// row from dbo.WMS_Generatebarcode plus the audit stamp.</summary>
public record RecentEncodingRow(
    long      BarcodeId,
    string    Barcode,
    string?   Itemname,
    string    Contno,
    string?   Brand,
    string?   Gender,
    string?   Color,
    string?   Size,
    string?   Style,
    string?   Division,
    string?   Department,
    string?   Class,
    string?   SubClass,
    string?   Family,
    DateTime? Trndate,
    string?   Tim1,
    DateTime  CreateTS,
    string?   CreatedBy);

/// <summary>
/// One row in the MH4 hierarchy dropdown: a distinct
/// (DivID, Division, Department, Class, Family, Subclass) combination
/// that exists in the picked container's items.
/// </summary>
public record Mh4Row(
    int     DivID,
    string  Division,
    string? Department,
    string? Class,
    string? Family,
    string? Subclass);

/// <summary>
/// Reads for the Item Encoding page. Save-side flow lands in Phase 3.
///
/// Sources:
///  - Open containers  → Azure dbo.WmsOpenUSACont (country-scoped, Closed &lt;&gt; 'Y').
///  - PONos            → distinct OraPONo on Azure dbo.WMS_ContAllocationData for the picked ContNo.
///  - Manifest brands  → distinct Brand on Azure dbo.WMS_ContAllocationData for the picked ContNo.
///  - Master brands    → Azure dbo.WMSBrandMaster (fallback search when manifest doesn't cover).
///  - Styles           → distinct Style on Azure dbo.WMS_ContAllocationData for the picked ContNo.
///  - MH4 hierarchy    → distinct (DivID, Division, Department, Class, Family, Subclass) from
///                        on-prem vupc_subclass × SubclassMaster for the picked ContNo's items.
///  - Gender / Color   → Azure dbo.WMSGender / dbo.WMSColor masters.
///  - Sizes            → Azure dbo.WMSSizeMaster filtered by DivID (MH4 must be picked first).
///  - Next SRNO        → MAX SRNO parsed off the tail of Barcode in dbo.WMS_Generatebarcode,
///                        keyed by (Contno, PONo prefix).
///  - Itemcode exists  → checks both dbo.WMS_ContAllocationData.Itemcode and
///                        dbo.WMS_Generatebarcode.Barcode.
/// </summary>
public class ItemEncodingService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int CommandTimeoutSeconds = 60;

    private string Country =>
        user.Country ?? throw new InvalidOperationException("Current user has no Country assigned.");

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    // ===================== Dropdowns =====================

    public async Task<List<OpenContnoRow>> GetOpenContainersAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<OpenContnoRow>(new CommandDefinition(@"
            SELECT ContNo   = contno,
                   ContDesc = contDesc
              FROM dbo.WmsOpenUSACont WITH (NOLOCK)
             WHERE Country = @country
               AND ISNULL(Closed,'N') = 'N'
             ORDER BY contno",
            new { country = Country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>All brands from the Azure master (WMSBrandMaster). Seeded from
    /// usa.dbo.BrandMaster.BrandName. Used as the searchable fallback list when
    /// the operator wants a brand not on the container's manifest.</summary>
    public async Task<List<BrandRow>> GetMasterBrandsAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BrandRow>(new CommandDefinition(@"
            SELECT Brand = BrandName
              FROM dbo.WMSBrandMaster WITH (NOLOCK)
             ORDER BY BrandName",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Distinct brands that appear on the picked container's manifest —
    /// pulled from Azure WMS_ContAllocationData.Brand for that ContNo.</summary>
    public async Task<List<BrandRow>> GetManifestBrandsForContnoAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BrandRow>(new CommandDefinition(@"
            SELECT DISTINCT Brand
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
             WHERE ContNo = @c AND Brand IS NOT NULL AND LTRIM(RTRIM(Brand)) <> ''
             ORDER BY Brand",
            new { c = contno.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Distinct PONos from Azure WMS_ContAllocationData for the container.</summary>
    public async Task<List<PONoRow>> GetPONosForContnoAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        await using var c = OpenWms();
        var rows = await c.QueryAsync<PONoRow>(new CommandDefinition(@"
            SELECT PONo = ORAPONo
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
             WHERE ContNo = @c AND ORAPONo IS NOT NULL AND LTRIM(RTRIM(ORAPONo)) <> ''
             GROUP BY ORAPONo
             ORDER BY ORAPONo",
            new { c = contno.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<StyleRow>> GetStylesForContnoAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        await using var c = OpenWms();
        var rows = await c.QueryAsync<StyleRow>(new CommandDefinition(@"
            SELECT DISTINCT Style
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
             WHERE ContNo = @c AND Style IS NOT NULL AND LTRIM(RTRIM(Style)) <> ''
             ORDER BY Style",
            new { c = contno.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Distinct MH4 combinations that appear on the container's items. Joins
    /// datareporting.vupc_subclass (per item → DivID/Division/Department/MH4ID)
    /// to SubclassMaster (per MH4ID → Class/Family/Subclass) filtered to items
    /// that appear in WMS_ContAllocationData for the given ContNo.
    /// </summary>
    public async Task<List<Mh4Row>> GetMh4ForContnoAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();

        // The items live on Azure (WMS_ContAllocationData); the MH4 lookup lives
        // on on-prem (vupc_subclass × SubclassMaster). Two-step: pull items from
        // Azure first, then resolve on on-prem.
        List<string> items;
        await using (var w = OpenWms())
        {
            items = (await w.QueryAsync<string>(new CommandDefinition(
                @"SELECT DISTINCT Itemcode
                    FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                   WHERE ContNo = @c AND Itemcode IS NOT NULL",
                new { c = contno.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        if (items.Count == 0) return new();

        await using var src = OpenOnPremBackup();
        var rows = await src.QueryAsync<Mh4Row>(new CommandDefinition(@"
            SELECT DISTINCT
                   v.DivID,
                   Division   = v.Division,
                   Department = v.Department,
                   [Class]    = s.[Class],
                   Family     = s.Family,
                   Subclass   = s.Subclass
              FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
              LEFT JOIN datareporting.dbo.SubclassMaster s WITH (NOLOCK)
                     ON s.MH4ID = v.MH4ID
             WHERE v.itemcode IN @items
               AND v.DivID IS NOT NULL
               AND v.Division IS NOT NULL
             ORDER BY v.Division, v.Department, s.[Class], s.Family, s.Subclass",
            new { items }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<GenderRow>> GetGendersAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<GenderRow>(new CommandDefinition(@"
            SELECT Gender FROM dbo.WMSGender WITH (NOLOCK) ORDER BY Gender",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ColorRow>> GetColorsAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<ColorRow>(new CommandDefinition(@"
            SELECT Color FROM dbo.WMSColor WITH (NOLOCK) ORDER BY Color",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<SizeRow>> GetSizesForDivIdAsync(int divId, CancellationToken ct = default)
    {
        if (divId <= 0) return new();
        await using var c = OpenWms();
        var rows = await c.QueryAsync<SizeRow>(new CommandDefinition(@"
            SELECT Size
              FROM dbo.WMSSizeMaster WITH (NOLOCK)
             WHERE DivID = @d
             ORDER BY Size",
            new { d = divId }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Generate Barcode + duplicate check =====================

    /// <summary>Compute the next SRNO for a (Contno, PONo) pair by scanning existing
    /// barcodes in dbo.WMS_Generatebarcode that match the "PONo-NNNN" prefix. If
    /// no prior barcode exists, returns 1. SRNO is stored on the barcode string
    /// itself (last 4 chars, zero-padded) so no additional column is needed.</summary>
    public async Task<int> GetNextBarcodeSrnoAsync(string contno, string pono, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno) || string.IsNullOrWhiteSpace(pono)) return 1;
        await using var c = OpenWms();
        var next = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT ISNULL(MAX(TRY_CAST(RIGHT(Barcode, 4) AS INT)), 0) + 1
              FROM dbo.WMS_Generatebarcode WITH (NOLOCK)
             WHERE Contno = @c
               AND Barcode LIKE @prefix",
            new { c = contno.Trim(), prefix = pono.Trim() + "-%" },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return next ?? 1;
    }

    /// <summary>Check whether an itemcode already exists on Azure — either as
    /// a barcode in WMS_Generatebarcode (already encoded) or as an itemcode in
    /// WMS_ContAllocationData (already allocated). Either hit blocks the save.</summary>
    public async Task<ItemcodeExistsResult> ItemcodeExistsAsync(string itemcode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemcode)) return new(false, false);
        await using var c = OpenWms();
        var alloc = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE Itemcode = @i",
            new { i = itemcode.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
        var gen = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMS_Generatebarcode WITH (NOLOCK) WHERE Barcode = @i",
            new { i = itemcode.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
        return new(alloc, gen);
    }

    // ===================== Recent Activity =====================

    /// <summary>Recent Item Encoding activity from dbo.WMS_Generatebarcode.
    /// When <paramref name="contnoFilter"/> is empty, only today's rows (GST)
    /// are returned. When a Contno is supplied, ALL rows for that Contno are
    /// returned (across dates). Newest first, capped at top rows.</summary>
    public async Task<List<RecentEncodingRow>> GetRecentEncodingsAsync(
        string? contnoFilter = null, int top = 200, CancellationToken ct = default)
    {
        var trimmed = (contnoFilter ?? "").Trim();
        await using var c = OpenWms();
        var sql = $@"
            SELECT TOP ({top})
                   BarcodeId,
                   Barcode,
                   Itemname,
                   Contno,
                   BRAND       AS Brand,
                   GENDER      AS Gender,
                   Color,
                   Size,
                   Style,
                   Division,
                   Department,
                   [Class]     AS [Class],
                   SubClass,
                   Family,
                   Trndate,
                   Tim1,
                   CreateTS,
                   CreatedBy
              FROM dbo.WMS_Generatebarcode WITH (NOLOCK)
             WHERE (@c = '' AND CAST(CreateTS AS DATE) = CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE))
                OR (@c <> '' AND Contno = @c)
             ORDER BY CreateTS DESC, BarcodeId DESC";
        var rows = await c.QueryAsync<RecentEncodingRow>(new CommandDefinition(
            sql, new { c = trimmed }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
