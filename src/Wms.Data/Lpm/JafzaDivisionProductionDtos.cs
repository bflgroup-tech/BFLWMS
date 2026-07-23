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

/// <summary>
/// JAFZA Robo Production Report — Summary. One row per (TrnDate, Division,
/// Username), Qty = SUM(Qty) from ROBOTICS.dbo.PairingConformationDetail
/// (via the JafazaRoboDb connection), ported from the legacy VB.NET "#pair"
/// desktop query.
/// </summary>
public record JafzaRoboProductionSummaryRow(
    DateTime TrnDate,
    string   Division,
    string   Username,
    int      Qty);

/// <summary>
/// JAFZA Robo Production Report — Detailed. One row per (TrnDate, ItemCode,
/// Username, GroupCode, GroupName, Division).
/// </summary>
public record JafzaRoboProductionDetailRow(
    DateTime TrnDate,
    string   ItemCode,
    string   Username,
    string?  GroupCode,
    string?  GroupName,
    string   Division,
    int      Qty);
