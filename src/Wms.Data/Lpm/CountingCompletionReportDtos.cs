namespace Wms.Data.Lpm;

/// <summary>
/// One row of the Counting Completion Report — Summary. One per (Country, ContNo);
/// LpmMonths / Divisions / Brands are comma-joined distinct values across the
/// container's underlying BuildingCompletionSumm/Det rows.
/// </summary>
public record CountingCompletionSummaryRow(
    string    Country,
    string    ContNo,
    DateTime? CountingCompletionDate,
    string?   PONo,
    DateTime? CountingStartDate,
    int       CountedQty,
    string?   LpmMonths,
    string?   Divisions,
    string?   Brands);
