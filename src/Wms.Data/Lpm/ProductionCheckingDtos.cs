namespace Wms.Data.Lpm;

/// <summary>Ported from LPMSIM. Per-batch breakdown row.</summary>
public sealed record ProductionCheckingRow(
    DateTime ProductionDay,
    long?    BatchNo,
    string   Kind,         // LPM / Non-LPM / Mixed / Unknown
    string   Division,
    long     TotalScanned,
    int      StoreQty);

/// <summary>Ported from LPMSIM. Summary-view row aggregated across batches.</summary>
public sealed record ProductionCheckingSummaryRow(
    DateTime ProductionDay,
    string   Kind,
    string   Division,
    long     TotalScanned,
    int      StoreQty,
    int      UaeStoreQty,
    int      OmanStoreQty,
    int      Ex2StoreQty,
    long     Ex2TotalScanned);

/// <summary>Store Qty for a single Ex2Locations shop, by its actual Country
/// (bfldata.dbo.DataSettings.Country -- KSA/QATAR/BAHRAIN/KUWAIT/MALAYSIA),
/// not the shared "Ex2Locations" SIMCountry bucket.</summary>
public sealed record Ex2ShopRow(
    string Country,
    int    StoreQty);

/// <summary>Bundle of detailed rows + summary rows + scalars returned in one go.</summary>
public sealed record ProductionCheckingResult(
    List<ProductionCheckingRow>        Rows,
    List<ProductionCheckingSummaryRow> Summary,
    int                                 OverallStoreQty,
    long                                TransferQty,
    List<Ex2ShopRow>                   Ex2Shops);

/// <summary>Merch Need (Month/Week/Day) totals for a country's current calendar month, from LPM_EOM_Output.</summary>
public sealed record MerchNeedRow(
    long MerchNeedMonth,
    long MerchNeedWeek,
    long MerchNeedDay);

/// <summary>Merch Need (Month/Week/Day), broken down per Division, for a country's current calendar month.</summary>
public sealed record MerchNeedDivisionRow(
    int  DivCode,
    string Division,
    long MerchNeedMonth,
    long MerchNeedWeek,
    long MerchNeedDay);
