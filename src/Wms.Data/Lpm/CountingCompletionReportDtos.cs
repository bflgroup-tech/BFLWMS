namespace Wms.Data.Lpm;

/// <summary>
/// One row of the Counting Completion Report — Summary. One per (Country, ContNo);
/// LpmMonths / Divisions are comma-joined distinct values across the
/// container's underlying BuildingCompletionSumm rows.
/// </summary>
public record CountingCompletionSummaryRow(
    string    Country,
    string    ContNo,
    DateTime? CountingCompletionDate,
    string?   PONo,
    DateTime? CountingStartDate,
    int       CountedQty,
    string?   LpmMonths,
    string?   Divisions);
