namespace Wms.Data.Lpm;

/// <summary>
/// PO Counting Report — one row per (Country, ContNo, PONumber). Unlike the
/// Counting Completion Report (grain = Country x ContNo, with PONumber as a
/// comma-joined list when a container carries multiple POs), this splits
/// that same container-level data down to one row per actual PO by
/// aggregating BFLDATA.dbo.BuildingCompletionDet_OraPONo (which carries a
/// single OraPONo per item row) instead of reading POnumber off
/// BuildingCompletionSumm directly.
///
/// OrderSheetQty/GRNQty/MissingQty/ExcessQty/ReturnToSuppQty/ReturnToBuyQty
/// are SUM(...) of the item-level Qty/CheckedQty/MissingQty/ExcessQty/
/// ReturnToSuppQty/ReturnToBuyQty columns for that PO within that container.
/// OrderQty is different — the real ordered quantity from
/// HODATA.dbo.Vusaorder (SUM(Qty) per (refno, ORAPONo), where refno is
/// Vusaorder's container-number column), not derived from
/// BuildingCompletionDet_OraPONo at all; 0 when that (ContNo, PO) pair has
/// no Vusaorder rows. ContainerFillRate is the same formula BuildingCompletionSumm uses
/// at container level (ContFillRatePer = TotalCheckedQty / TotalQty * 100),
/// applied per PO instead: GRNQty / OrderSheetQty * 100, 0 when
/// OrderSheetQty is 0 (OrderSheetQty, not the Vusaorder-based OrderQty, to
/// match what BuildingCompletionSumm itself uses as its denominator).
///
/// ErrorUnits/ErrorRate/PurchaseType/Remarks/Status/Buyer are also
/// container-level (BuildingCompletionSumm.ContErrorUnits/ContErrorrate/
/// Purchasetype/remarks/status/buyer) — same value repeats for every PO
/// within a given container, same caveat as Division/Supplier.
/// </summary>
public record PoCountingRow(
    string    Country,
    string    ContNo,
    string    PONumber,
    DateTime? CountingCompletionDate,
    string?   Division,
    string?   Supplier,
    int       OrderSheetQty,
    int       OrderQty,
    int       GRNQty,
    decimal   ContainerFillRate,
    int       MissingQty,
    decimal   PctMissing,
    int       ExcessQty,
    decimal   PctExcess,
    int       ReturnToSuppQty,
    decimal   PctMissingReturn,
    int       ReturnToBuyQty,
    decimal   ErrorUnits,
    decimal   ErrorRate,
    string?   PurchaseType,
    string?   Remarks,
    string?   Status,
    string?   Buyer);

/// <summary>
/// PO Counting Report — Item-wise detail. One row per item (upc) for a given
/// (ContNo, PONumber), from BFLDATA.dbo.BuildingCompletionDet_OraPONo. Shown
/// on double-clicking a PO row in the summary grid.
/// </summary>
public record PoCountingItemRow(
    string    ContNo,
    string    PONumber,
    string    ItemCode,
    string?   ItemName,
    string?   Style,
    string?   PalletType,
    string?   Brand,
    DateTime? LpmDt,
    int       Qty,
    int       CheckedQty,
    int       MissingQty,
    int       ExcessQty,
    int       ReturnToSuppQty,
    int       ReturnToBuyQty);
