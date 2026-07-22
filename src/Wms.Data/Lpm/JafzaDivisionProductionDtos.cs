namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Manual Production Report — Summary. One row per (TrnDate, Division,
/// Username), CheckedQty = SUM(CheckedQty) from Online.dbo.PhotoChecking.
/// </summary>
public record JafzaProductionSummaryRow(
    DateTime TrnDate,
    string   Division,
    string   Username,
    int      CheckedQty);

/// <summary>
/// JAFZA Division-wise Production Report — Detailed. One row per (TrnDate,
/// UPC, Username, GroupCode, Division, LPMDT, OraPONo).
/// </summary>
public record JafzaProductionDetailRow(
    DateTime TrnDate,
    string   UPC,
    string   Username,
    string   GroupCode,
    string   Division,
    int      CheckedQty,
    DateTime? Lpmdt,
    string?  OraPoNo);
