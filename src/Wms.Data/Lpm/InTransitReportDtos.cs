namespace Wms.Data.Lpm;

public record InTransitReportRow(
    string   Country,
    string   GinNo,
    DateTime ReleasedDate,
    DateTime? EtaDate,
    string   ShipNo,
    int      TotalQty,
    int      TransferCount,
    string   Whouse,
    string   Remarks,
    string   Division,
    string   Department,
    string   Brand
);

public record InTransitReportFilter(
    string?  Country,   // null/"" = "BFL Group" (all countries)
    DateTime Since       // ExportPass.Trndate lower bound, to bound the scan
);
