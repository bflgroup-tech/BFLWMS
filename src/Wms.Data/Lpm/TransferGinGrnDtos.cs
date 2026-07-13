namespace Wms.Data.Lpm;

public record TransferHistoryRow(
    int     SrNo,
    string  ShopName,
    string  TrfNo,
    DateTime TrfDate,
    string? PalletNo,
    DateTime? BuildDate,
    string?  GINNo,
    DateTime? GINDate,
    string?  GRNNo,
    DateTime? GRNDate,
    string   Remarks
);

public record TransferHistoryFilter(
    string    Country,
    string?   Store,
    DateTime  DateFrom,
    DateTime  DateTo,
    bool      WithoutPallet,
    bool      WithoutGin,
    bool      WithoutGrn,
    string    SearchBy,    // "TrfNo" | "PalletNo" | "GIN" | "GRN"
    string?   SearchValue
);
