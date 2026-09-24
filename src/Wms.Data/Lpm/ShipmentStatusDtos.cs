namespace Wms.Data.Lpm;

public record ShipmentStatusRow(
    string    Country,
    string    ShipNo,
    string    Type,        // "JAFZA" | "LOCAL" | "International" — derived from ShipNo (UI label: Source Location)
    string    Status,      // "Delivered" once a receipt record exists, else "InTransit"
    string    GinNo,       // blank for LOCAL/International (no GIN/export flow)
    DateTime? GinDate,
    DateTime? ReleasedOn,
    int?      SlaShippingDays,   // GinDate -> ReleasedOn
    DateTime? Eta,
    int       TotalQty,
    int?      BoxCount,         // TransferCount; null for LOCAL/International
    DateTime? ReceiptDt,        // null while InTransit
    int?      SlaReceiptDays,   // ReleasedOn -> ReceiptDt
    int?      ReceivedBoxes,    // null for LOCAL/International
    int?      BoxCountDiff,     // BoxCount - ReceivedBoxes; null for LOCAL/International
    string    Remarks,
    string    Division,
    string    Department,
    string    Brand
);

public record ShipmentStatusFilter(
    IReadOnlyList<string>? Countries,  // null/empty = "BFL Group" (all countries)
    DateTime DateFrom,                 // ReceiptDt range for delivered shipments
    DateTime DateTo                    // shipments still in transit as of DateTo are included regardless of DateFrom
);

public record ShipmentStatusResult(
    List<ShipmentStatusRow>     Rows,
    List<string>                Warnings,        // one entry per country that failed during a "BFL Group" fan-out
    List<ExportTransferSummary> ExportTransfers  // one entry per country — from racks..InTransit_ExportShipment
);

// Distinct TrfNo count, Quantity, and distinct ShipNo count from
// racks..InTransit_ExportShipment (JOIN P2EXPORT..vTransferDetail), split by its own
// Intransit flag: 'Y' shown on the JAFZA card's Intransit section, 'C' shown as
// Reserved. Excludes any TrfNo already present in that country's own VerifyGin
// table (already verified/received at destination, even if this source hasn't
// caught up yet). 'R' is shown as Received, scoped to CreateDate within the selected
// Receipt Date range (no VerifyGin exclusion). None has its own JAFZA/LOCAL/International split — the
// source table only carries Country + TrfNo, no ShipNo of its own to derive Type.
public record ExportTransferSummary(
    string Country,
    int IntransitTrfCount, int IntransitQty, int IntransitShipCount,
    int ReservedTrfCount,  int ReservedQty,  int ReservedShipCount,
    int ReceivedTrfCount,  int ReceivedQty,  int ReceivedShipCount);

// Division x Month (by vTransferDetail.LpmDt) drill-down pivot, shown as a popup when
// an Intransit number or a GIN No. is clicked. MonthQty on each row is index-aligned
// with MonthLabels; MonthTotals is the Grand Total row.
public record DivisionMonthSummaryResult(
    List<string>          MonthLabels,
    List<DivisionMonthRow> Rows,
    List<decimal>         MonthTotals,
    decimal               GrandTotal
);

public record DivisionMonthRow(
    string        Division,
    List<decimal> MonthQty,
    decimal       RowTotal
);

// Row-level detail shown as a popup when the Received/Intransit/Reserved title icon on
// the JAFZA card is clicked — one row per (TrfNo, Division) rather than the
// Division x Month collapse above. TransferDate is transferheader.TrfDate (when the
// transfer was created); Lpm is vTransferDetail.LpmDt (same date already used for the
// Division/Month pivot elsewhere on this page).
// One row per (ShipNo, GIN) behind the JAFZA card's Ship Count figures.
public record ExportShipmentRow(
    string    Country,
    string?   ShipNo,
    string?   GinNo,
    DateTime? GinDate,
    int       TrfCount,
    decimal   Qty);

public record TransferDetailRow(
    string    Country,
    string?   StoreId,
    string    TrfNo,
    DateTime? TransferDate,
    string?   Division,
    DateTime? Lpm,
    decimal   Qty);
