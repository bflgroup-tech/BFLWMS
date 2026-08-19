namespace Wms.Data.Lpm;

/// <summary>One division's weekly max capacity for a warehouse (LPMSIM.dbo.WMS_WH_MAXMIN_CAP,
/// most recent uploaded week) — Division is that table's own (ALL CAPS) spelling, which is the
/// authoritative "every division for this warehouse" list (it includes ad-hoc entries like
/// "DATA MIGRATION"/"LFL" that the Division master table doesn't have).</summary>
public sealed record DivisionCapRow(string Division, decimal MaxCapWeek);

/// <summary>One (Division, Week) merch_need target cell from LPMSIM.dbo.BFL_MFP_OUTBOUND_T1,
/// resolved from its integer division code via the Division master table — Division here is
/// that table's Title Case spelling; match against DivisionCapRow.Division case-insensitively.</summary>
public sealed record DivisionWeekTargetRow(string Division, int Week, decimal Target);

public sealed record DivisionCapVsTargetResult(List<DivisionCapRow> Caps, List<DivisionWeekTargetRow> Targets);
