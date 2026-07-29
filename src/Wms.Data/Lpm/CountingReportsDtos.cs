namespace Wms.Data.Lpm;

/// <summary>One row in the Pending Purchase (GRN) Status report — containers
/// counted (bfldata.BuildingCompletion) but not yet purchased (usa.usapurchase).</summary>
public record PendingPurchaseRow(
    string   ContNo,
    DateTime CountingDate,
    string?  TrnTime,
    int      CountedQty,
    int      AgeingDays,
    string   Divisions);

/// <summary>One row in the Purchased Containers list — containers counted from
/// 2026-01-01 onwards that HAVE landed in usa.usapurchase, with the purchase
/// date/time (earliest usapurchase row per ContNo) alongside the counting
/// date/time. Used as the second section of the Pending Goods Receipt email.</summary>
public record PurchasedContainerRow(
    string   ContNo,
    DateTime CountingDate,
    string?  CountingTime,
    int      CountedQty,
    DateTime PurchaseDate,
    string?  PurchaseTime,
    int      DaysToPurchase,
    string   Divisions);
