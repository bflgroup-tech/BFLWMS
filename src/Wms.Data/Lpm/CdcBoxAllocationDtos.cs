namespace Wms.Data.Lpm;

/// <summary>One selectable LPM month, with the eligible DC stock behind it.</summary>
public sealed record CdcLpmOption(
    DateTime LpmDt,
    string   Label,     // "Mar-2026"
    int      Boxes,
    int      Items,
    long     Qty);

/// <summary>
/// A store row out of WmsOtsCdcAllocationRun, plus the Volume Group overlaid
/// from StoreDivGrade. Mirrors the private lookup row PO allocation uses —
/// kept separate rather than shared, because the two read different tables and
/// coupling them would make a column change to one silently reshape the other.
/// </summary>
public sealed class CdcOtsStoreRow
{
    public string  Country         { get; set; } = "";
    public string  StoreID         { get; set; } = "";
    public int     DivCode         { get; set; }
    public string? VolumeGroup     { get; set; }
    public int     TgtEOM          { get; set; }
    public int     SOHToday        { get; set; }
    public int     WeekSales       { get; set; }
    public int     InTransit       { get; set; }
    public int     Ex2DcSoh        { get; set; }
    public int     CountingWIP     { get; set; }
    public int     OtsQtyToday     { get; set; }
    public decimal OtsPercentToday { get; set; }
    public int     CurrentEOW      { get; set; }
}

/// <summary>One (SKU, Store) row of DC_STORE_SOH_ALLOCATION.</summary>
public sealed record CdcDcSohAllocationRow(
    string   Itemcode,
    int      DivCode,
    string   Country,
    string   StoreID,
    string?  VolumeGroup,
    int      DcQty,          // total eligible DC qty for the SKU — the band basis
    decimal  OtsPercent,     // store's OtsPercentToday
    decimal  AvgOtsPercent,  // division average of positive OTS% for this SKU
    string   TierName,       // MinMin / MinMax / IdealMax / MaxMax
    int      SkuMaxTier,     // the tier value the band gave
    int      StoreSoh,       // existing LPM_locstock SOH, subtracted from the tier
    int      AllocatedSoh);  // what this store actually got

/// <summary>Header of the run currently sitting in DC_STORE_SOH_ALLOCATION.</summary>
public sealed record CdcDcSohAllocationHeader(
    DateTime RunTS,
    string?  RunBy,
    string?  LpmScope,
    string?  CountryScope,
    int      RowCount,
    long     TotalAllocated);

/// <summary>Per-country roll-up of the current allocation.</summary>
public sealed record CdcDcSohCountrySummaryRow(
    string Country,
    int    Stores,
    int    Skus,
    long   Allocated);

/// <summary>Outcome of one "Allocate DC SOH to Stores" run.</summary>
public sealed record CdcDcSohAllocationResult(
    bool          Success,
    string?       Message,
    DateTime      RunTS,
    string?       RunBy,
    string?       LpmScope,
    string?       CountryScope,
    int           SkuCount,
    int           StoreRowCount,
    long          TotalDcQty,
    long          TotalAllocated,
    long          Unallocated,
    List<string>  Warnings)
{
    public static CdcDcSohAllocationResult Fail(string message) =>
        new(false, message, default, null, null, null, 0, 0, 0, 0, 0, new List<string>());
}
