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
    int      Ex2StoreQty);

/// <summary>Bundle of detailed rows + summary rows + scalars returned in one go.</summary>
public sealed record ProductionCheckingResult(
    List<ProductionCheckingRow>        Rows,
    List<ProductionCheckingSummaryRow> Summary,
    int                                 OverallStoreQty,
    long                                TransferQty);

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
