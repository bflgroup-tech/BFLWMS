namespace Wms.Data.Lpm;

/// <summary>
/// Counting Completion Report — "Today" mode, Summary. One row per
/// (Country, ContNo); LpmDates/Divisions/OraPoNos/Brands are comma-joined
/// distinct values across the container's Online.dbo.PhotoCheckingResult
/// rows (QtyIssue > 0) for today.
/// </summary>
public record CountingCompletionTodaySummaryRow(
    string  Country,
    string  ContNo,
    int     CountedQty,
    string? LpmDates,
    string? Divisions,
    string? OraPoNos,
    string? Brands,
    string? PalletTypes,
    string? TypeNames);

/// <summary>
/// Counting Completion Report — "Today" mode, Allocation-wise. Same shape as
/// CountingCompletionTodaySummaryRow, broken down further by ResultType (the
/// PhotoCheckingResult equivalent of Box Category / PalletType) — one row per
/// (Country, ContNo, ResultType). TypeName is the friendly display name from
/// BFLDATA.dbo.PalletType, keyed by ResultType.
/// </summary>
public record CountingCompletionTodayAllocationRow(
    string  Country,
    string  ContNo,
    string  ResultType,
    string? TypeName,
    int     BuildQty,
    string? LpmDates,
    string? Divisions,
    string? OraPoNos,
    string? Brands);

/// <summary>
/// Counting Completion Report — "Today" mode, Detailed. One row per
/// (Country, ContNo, UPC). Division/LpmDt/OraPoNo are the item's own values
/// (not comma-joined — this is item-level, unlike Summary/Allocation-wise).
/// </summary>
public record CountingCompletionTodayDetailRow(
    string    Country,
    string    ContNo,
    string    ItemCode,
    string?   ItemName,
    int       Qty,
    DateTime? LpmDt,
    string?   Division,
    string?   OraPoNo,
    string?   Brand,
    string?   PalletType,
    string?   TypeName);
