namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Division-wise Production Report — Summary. One row per (TrnDate,
/// Division), CheckedQty = SUM(CheckedQty) from Online.dbo.PhotoChecking.
/// </summary>
public record JafzaProductionSummaryRow(
    DateTime TrnDate,
    string   Division,
    int      CheckedQty);

/// <summary>
/// JAFZA Division-wise Production Report — Detailed. One row per (TrnDate,
/// UPC, Username, GroupCode, Division, Size).
/// </summary>
public record JafzaProductionDetailRow(
    DateTime TrnDate,
    string   UPC,
    string   Username,
    string   GroupCode,
    string   Division,
    string?  Size,
    int      CheckedQty);
