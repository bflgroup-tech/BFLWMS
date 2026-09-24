namespace Wms.Data.Notifications;

/// <summary>
/// Single-row config for the weekly RD/RS ageing email. Schedule is a day plus a
/// GST wall-clock time rather than an interval — the business wants it on a
/// specific morning, not every N hours.
/// </summary>
public record RdRsAgeingEmailConfig(
    int       Id,
    string    Recipients,        // comma or semicolon separated To addresses
    string?   CcRecipients,      // optional Cc, same format
    byte      DayOfWeek,         // 0 = Sunday .. 6 = Saturday (System.DayOfWeek)
    byte      HourGst,           // 0..23, GST wall clock
    byte      MinuteGst,         // 0..59
    bool      IsActive,
    DateTime? LastRunDate,       // GST date of the last attempt — the fire-once-per-day guard
    DateTime? LastRunTS,
    string?   LastRunStatus,
    int?      LastSentRows,
    long?     LastSentQty,
    DateTime  UpdatedTS,
    string?   UpdatedBy);
