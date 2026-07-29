namespace Wms.Data.Lpm;

/// <summary>One row in the Pending Purchase (GRN) Status report — containers
/// counted (bfldata.BuildingCompletion) but not yet purchased (usa.usapurchase).</summary>
public record PendingPurchaseRow(
    string   ContNo,
    DateTime CountingDate,
    int      CountedQty,
    int      AgeingDays,
    string   Divisions);

/// <summary>One row in the Purchased Containers list — containers counted from
/// 2026-01-01 onwards that HAVE landed in usa.usapurchase, with the purchase
/// date/time (earliest usapurchase row per ContNo, from usapurchase.Tmdate +
/// Time1) alongside the counting date. Used as the second section of the
/// Pending Goods Receipt email. Counting time is intentionally absent — the
/// source table bfldata.BuildingCompletion has no time column, only Trndate.</summary>
public record PurchasedContainerRow(
    string   ContNo,
    DateTime CountingDate,
    int      CountedQty,
    DateTime PurchaseDate,
    string?  PurchaseTime,
    int      DaysToPurchase,
    string   Divisions);
