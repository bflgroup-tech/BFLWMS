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

/// <summary>
/// Division/Department/GroupCode/Brand breakdown, shown as a popup when a summary
/// stat card is clicked. Always sourced from vTransferDetail (the only table with
/// item-line GroupCode) — the GIN flow has no line-item GroupCode of its own, so
/// its breakdown ("based on GIN") is scoped to the transfers that have at least one
/// linked GIN, same approximation ShipmentStatusService already uses for its own
/// Division/Department/Brand rollup on the GIN/BFL flow.
/// </summary>
public record TransferGinBreakdownRow(string Division, string Department, string GroupCode, string Brand, int Qty);

public record TransferGinBreakdownResult(List<TransferGinBreakdownRow> Rows, int GrandQty, List<string> Warnings);
