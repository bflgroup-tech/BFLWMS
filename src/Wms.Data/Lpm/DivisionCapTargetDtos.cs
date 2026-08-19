namespace Wms.Data.Lpm;

/// <summary>One division's weekly max capacity for a warehouse (LPMSIM.dbo.WMS_WH_MAXMIN_CAP,
/// most recent uploaded week) — Division is that table's own (ALL CAPS) spelling, which is the
/// authoritative "every division for this warehouse" list (it includes ad-hoc entries like
/// "DATA MIGRATION"/"LFL" that the Division master table doesn't have).</summary>
public sealed record DivisionCapRow(string Division, decimal MaxCapWeek);

/// <summary>One division's merch_need target for the selected (Year, Week) from
/// LPMSIM.dbo.BFL_MFP_OUTBOUND_T1, resolved from its integer division code via the Division
/// master table — Division here is that table's Title Case spelling; match against
/// DivisionCapRow.Division case-insensitively.</summary>
public sealed record DivisionWeekTargetRow(string Division, decimal Target);

/// <summary>One (Division, Day) actual production quantity for the selected week — same
/// source tables as the Daily Transfer Qty by Warehouse report (bfldata.dbo.DailyCountCategoryTrf
/// for UAE/Techno, that country's own dbo.vTransferDetail for export countries), grouped by
/// Division instead of by warehouse total.</summary>
public sealed record DivisionDailyRow(string Division, DateTime Day, long Quantity);

public sealed record DivisionCapVsTargetResult(
    List<DivisionCapRow> Caps, List<DivisionWeekTargetRow> Targets, List<DivisionDailyRow> Daily);
