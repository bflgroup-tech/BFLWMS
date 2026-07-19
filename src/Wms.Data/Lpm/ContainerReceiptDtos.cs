namespace Wms.Data.Lpm;

public record ContainerReceiptRow(
    string   Warehouse,
    string   ContNo,
    string   GinNo,
    DateTime ReceiptDt,
    string   InvoiceNo,
    string   ReceivedBy,
    string   SuppCode,
    int      ShipmentQty,
    int      BoxCount,
    int      GRNDone
);

public record ContainerReceiptFilter(
    string?  Warehouse,   // null/"" = "BFL Group" (all warehouses)
    DateTime DateFrom,
    DateTime DateTo
);

public record ContainerReceiptResult(
    List<ContainerReceiptRow> Rows,
    List<string>              Warnings   // one entry per warehouse that failed during a "BFL Group" fan-out
);
