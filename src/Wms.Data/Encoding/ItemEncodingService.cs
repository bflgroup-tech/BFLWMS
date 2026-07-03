using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Encoding;

public record OpenContnoRow(string ContNo, string? ContDesc);
public record BrandRow(string Brand);
public record StyleRow(string Style);
public record GenderRow(string Gender);
public record ColorRow(string Color);
public record SizeRow(string Size);

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
///  - Brands           → on-prem usa.dbo.BrandMaster.
///  - Styles           → distinct Style on Azure dbo.WMS_ContAllocationData for the picked ContNo.
///  - MH4 hierarchy    → distinct (DivID, Division, Department, Class, Family, Subclass) from
///                        on-prem vupc_subclass × SubclassMaster for the picked ContNo's items.
///  - Gender / Color   → Azure dbo.WMSGender / dbo.WMSColor masters.
///  - Sizes            → Azure dbo.WMSSizeMaster filtered by DivID (MH4 must be picked first).
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

    public async Task<List<BrandRow>> GetBrandsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BrandRow>(new CommandDefinition(@"
            SELECT DISTINCT Brand
              FROM usa.dbo.BrandMaster WITH (NOLOCK)
             WHERE Brand IS NOT NULL AND LTRIM(RTRIM(Brand)) <> ''
             ORDER BY Brand",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
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
}
