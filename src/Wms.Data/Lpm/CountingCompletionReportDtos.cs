namespace Wms.Data.Lpm;

/// <summary>
/// One row of the Counting Completion Report — Summary. One per (Country, ContNo);
/// LpmMonths / Divisions / Brands are comma-joined distinct values across the
/// container's underlying BuildingCompletionSumm/Det rows.
/// </summary>
public record CountingCompletionSummaryRow(
    string    Country,
    string    ContNo,
    DateTime? CountingCompletionDate,
    DateTime? PurchaseDate,
    string?   PONo,
    DateTime? CountingStartDate,
    int       CountedQty,
    string?   LpmMonths,
    string?   Divisions,
    string?   Brands);

/// <summary>
/// Counting Completion Report — Detailed. One row per (Country, ContNo,
/// ItemCode, PalletType) from BFLDATA.dbo.BuildingCompletionDet — Box
/// Category (Pallettype), Item Code/Name, and Qty (CheckedQty) all come
/// directly off that single table. Division is the item's own division
/// (from Datareporting.dbo.vUPC_SUBCLASS via ItemCode = upc), not the
/// container-level list used by Summary/Allocation-wise.
/// </summary>
public record CountingCompletionDetailRow(
    string    Country,
    string    ContNo,
    DateTime? PurchaseDate,
    string?   PalletType,
    string?   TypeName,
    string    ItemCode,
    string?   ItemName,
    int       Qty,
    string?   LpmMonths,
    string?   Division,
    string?   Brand);

/// <summary>
/// Counting Completion Report — Detailed / Allocation-wise. Same shape as
/// CountingCompletionSummaryRow, but broken down further by PalletType (Box
/// Category): one row per (Country, ContNo, PalletType) instead of one row
/// per (Country, ContNo). BuildQty is SUM(CheckedQty) for that container +
/// box category. No ItemCode/ItemName — those belong to the Item-wise view.
/// </summary>
public record CountingAllocationRow(
    string    Country,
    string    ContNo,
    DateTime? CountingCompletionDate,
    DateTime? PurchaseDate,
    string?   PONo,
    DateTime? CountingStartDate,
    string?   PalletType,
    string?   TypeName,
    int       BuildQty,
    string?   LpmMonths,
    string?   Divisions,
    string?   Brands);
