namespace Wms.Data.Lpm;

/// <summary>One row in the Counting Report Summary — one per (ContNo, PONo).</summary>
public record CountingSummaryRow(
    string    ContNo,
    string?   PONo,
    DateTime? ReceiptDt,
    DateTime? CountingStartedDt,
    int       OrderSheetQty,
    int       CountedQty,
    int       RtvQty,
    int       McHoldQty,
    DateTime? CountingCompletedDt,
    DateTime? PurchaseDt);

/// <summary>One row in the Counting Report Detail — one per WmsUPCBoxDet aggregate.</summary>
public record CountingDetailRow(
    string   ContNo,
    string?  PONo,
    string   BoxNo,
    DateTime? LpmDt,
    string?  ToteId,
    string?  PalletType,
    string?  PalletTypeName,
    string?  StoreId,
    string?  Itemcode,
    int      Qty);

/// <summary>One row in the Cont Counting Production Report.</summary>
public record CountingProductionRow(
    string   ContNo,
    DateTime TrnDate,
    string?  UserName,
    int      Qty);
