namespace Wms.Data.Lpm;

public record TransferHistoryRow(
    long    SrNo,
    string  ShopName,
    string  TrfNo,
    DateTime TrfDate,
    int     Quantity,
    string? PalletNo,
    DateTime? BuildDate,
    string?  GINNo,
    DateTime? GINDate,
    string?  GRNNo,
    DateTime? GRNDate,
    string   Remarks
);

public record TransferHistoryFilter(
    List<string>? Countries,   // null/empty = every SIM country ("BFL Group")
    string?   Store,
    DateTime  DateFrom,
    DateTime  DateTo,
    bool      WithoutPallet,
    bool      WithoutGin,
    bool      WithoutGrn,
    string    SearchBy,    // "TrfNo" | "PalletNo" | "GIN" | "GRN"
    string?   SearchValue
);

public record TransferHistoryResult(List<TransferHistoryRow> Rows, List<string> Warnings);

/// <summary>Transfer/Transfer Qty/GIN Count/GIN Qty totals for one country, for the summary cards.</summary>
public record TransferSummary(string Country, int TransferCount, int TransferQty, int GinCount, int GinQty);

public record TransferSummaryResult(List<TransferSummary> Summaries, List<string> Warnings);
