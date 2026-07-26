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
