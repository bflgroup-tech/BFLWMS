namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Manual Production Report — Summary. One row per (TrnDate, Division,
/// Username), CheckedQty = SUM(CheckedQty) from Online.dbo.PhotoChecking.
/// </summary>
public record JafzaProductionSummaryRow(
    DateTime TrnDate,
    string   Division,
    string   Username,
    int      CheckedQty);

/// <summary>
/// JAFZA Division-wise Production Report — Detailed. One row per (TrnDate,
/// UPC, Username, GroupCode, Division, LPMDT, OraPONo).
/// </summary>
public record JafzaProductionDetailRow(
    DateTime TrnDate,
    string   UPC,
    string   Username,
    string   GroupCode,
    string   Division,
    int      CheckedQty,
    DateTime? Lpmdt,
    string?  OraPoNo);

/// <summary>
/// JAFZA Robo Production Report — Summary. One row per (TrnDate, Division,
/// Username), Qty = SUM(Qty) from ROBOTICS.dbo.PairingConformationDetail
/// (via the JafazaRoboDb connection), ported from the legacy VB.NET "#pair"
/// desktop query.
/// </summary>
public record JafzaRoboProductionSummaryRow(
    DateTime TrnDate,
    string   Division,
    string   Username,
    int      Qty);

/// <summary>
/// JAFZA Robo Production Report — Detailed. One row per (TrnDate, ItemCode,
/// Username, GroupCode, GroupName, Division).
/// </summary>
public record JafzaRoboProductionDetailRow(
    DateTime TrnDate,
    string   ItemCode,
    string   Username,
    string?  GroupCode,
    string?  GroupName,
    string   Division,
    int      Qty);

/// <summary>
/// JAFZA Export Production — Summary. One row per (TrnDate, Division,
/// ShopName), Qty = SUM(all hour buckets, hr1a..hr22a + HR0A) from
/// BFLDATA.dbo.DailyCountCategoryTrf (Warehouse = 'JAFZA'). No item-level
/// detail is available from this source — Summary only.
/// </summary>
public record JafzaExportProductionRow(
    DateTime TrnDate,
    string   Division,
    string   ShopName,
    int      Qty);

/// <summary>
/// JAFZA Box GRN Production — Summary. One row per (TrnDate, Division):
/// BoxCount = COUNT(DISTINCT BoxNo), Qty = SUM(Qty), from
/// USA.dbo.vUPCBoxDet (WHouse = 'JAFZA', Remarks = 'Box GRN'). Division
/// comes from USA.dbo.USAPriority.DivisionY via the view's own GroupCode.
/// </summary>
public record JafzaBoxGrnSummaryRow(
    DateTime TrnDate,
    string   Division,
    int      BoxCount,
    int      Qty);

/// <summary>
/// JAFZA Box GRN Production — Detailed. One row per (TrnDate, ItemCode,
/// GroupCode, Division): BoxCount = COUNT(DISTINCT BoxNo) for that item.
/// </summary>
public record JafzaBoxGrnDetailRow(
    DateTime TrnDate,
    string   ItemCode,
    string?  GroupCode,
    string   Division,
    int      BoxCount,
    int      Qty);

/// <summary>
/// Business-week option for the JAFZA Production Report's Week filter, from
/// LPMSIM.dbo.BFL_MFP_OUTBOUND_T1's fiscal (year, week) pair. That table
/// carries no per-week date column at all (only a batch-load createts shared
/// by every row), so OtsDate here is computed in C# as the Sunday starting
/// that fiscal week: FirstSundayOfJanuary(year) + (week-1)*7 days — confirmed
/// against a known example (fiscal week 31 of 2026 = 02-Aug through
/// 08-Aug-2026). This is an inferred rule (no authoritative fiscal-calendar
/// table was found in LPMSIM/BFLDATA/USA), not a value read off a stored
/// date, so it should be spot-checked against other known weeks if BFL's
/// fiscal year start rule turns out to differ. The business week is the full
/// Sunday-through-Saturday span, always 7 days, within the report's existing
/// 7-day range cap.
/// </summary>
public record JafzaWeekOption(int Wk, DateTime OtsDate)
{
    public DateTime WeekStart => OtsDate.AddDays(-(int)OtsDate.DayOfWeek);
    public DateTime WeekEnd   => WeekStart.AddDays(6);
}
