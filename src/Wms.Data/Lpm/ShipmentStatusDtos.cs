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
    List<ShipmentStatusRow> Rows,
    List<string>            Warnings   // one entry per country that failed during a "BFL Group" fan-out
);

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
