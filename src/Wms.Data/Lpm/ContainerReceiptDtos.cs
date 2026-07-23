namespace Wms.Data.Lpm;

public record ContainerReceiptRow(
    string    Country,
    string    GinNo,
    DateTime? GinDate,
    DateTime? ReleasedOn,
    int?      GinToExportPassDays,
    string    ShipNo,
    int       TotalQty,
    int       TransferCount,
    DateTime  ReceiptDt,
    int?      ReleasedOnToReceiptDtDays,
    int       ReceivedBoxes,
    int       BoxCountDiff
);

public record ContainerReceiptFilter(
    string?  Country,   // null/"" = "BFL Group" (all countries)
    DateTime DateFrom,
    DateTime DateTo
);

public record ContainerReceiptResult(
    List<ContainerReceiptRow> Rows,
    List<string>              Warnings   // one entry per country that failed during a "BFL Group" fan-out
);
