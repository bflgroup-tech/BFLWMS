namespace Wms.Data.Notifications;

/// <summary>Single-row config for the Pending Goods Receipt scheduled email.
/// Backed by dbo.WmsPendingGoodsReceiptEmailConfig on Azure WMS DB.</summary>
public record PendingGoodsReceiptEmailConfig(
    int       Id,
    string    Recipients,        // comma or semicolon separated To addresses
    int       IntervalHours,
    bool      IsActive,
    DateTime? LastRunTS,
    string?   LastRunStatus,
    int?      LastSentCount,
    DateTime  UpdatedTS,
    string?   UpdatedBy);
