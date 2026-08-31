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

/// <summary>Transfer Qty from bfldata.dbo.DailyCountCategoryTrf, by actual Country
/// (bfldata.dbo.DataSettings.Country) and date -- one row per (Country, Date) for
/// KSA/QATAR/BAHRAIN/KUWAIT/MALAYSIA (Ex2Locations shops, via Warehouse=JAFZA) plus
/// UAE/OMAN (via Warehouse=TECHNO, the same warehouse the overall Transfer Qty scalar
/// reads, just split by country/date).</summary>
public sealed record Ex2ShopRow(
    string   Country,
    DateTime Date,
    long     TransferQty);

/// <summary>Bundle of detailed rows + summary rows + scalars returned in one go.</summary>
public sealed record ProductionCheckingResult(
    List<ProductionCheckingRow>        Rows,
    List<ProductionCheckingSummaryRow> Summary,
    int                                 OverallStoreQty,
    long                                TransferQty,
    List<Ex2ShopRow>                   Ex2Shops);

/// <summary>Merch Need (Month/Week/Day) totals for a country and selected week, from
/// LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 (see ReportsService.GetMerchNeedAsync).</summary>
public sealed record MerchNeedRow(
    long MerchNeedMonth,
    long MerchNeedWeek,
    long MerchNeedDay);

/// <summary>Merch Need (Month/Week/Day), broken down per Division, for a country and selected week.</summary>
public sealed record MerchNeedDivisionRow(
    int  DivCode,
    string Division,
    long MerchNeedMonth,
    long MerchNeedWeek,
    long MerchNeedDay);
