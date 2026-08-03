/*
 * Renames two columns on dbo.WmsOtsPoAllocationRun (idempotent):
 *   WkReduction    -> WeekAdjustment
 *   WeeksToInclude -> NoOfLeadWeeks
 *
 * WkReduction was misleading — the value could be positive OR negative
 * depending on whether the store is scaling up (Tgt > Prev) or winding
 * down (Tgt < Prev). "Adjustment" fits both directions.
 *
 * WeeksToInclude renamed to NoOfLeadWeeks so it reads as "how many
 * weeks of lead the interpolation projects forward from Prev Month EOM."
 *
 * The formulas themselves also change alongside this rename (see the
 * code change accompanying this migration):
 *   WeekAdjustment  =  (TgtEOM - PrevMonthEOM) / weeksInPrevMonth
 *   CurrentEOW      =  PrevMonthEOM + (WeekAdjustment * NoOfLeadWeeks)
 */
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'WkReduction') IS NOT NULL
   AND COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'WeekAdjustment') IS NULL
BEGIN
    EXEC sp_rename N'dbo.WmsOtsPoAllocationRun.WkReduction', N'WeekAdjustment', N'COLUMN';
END;
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'WeeksToInclude') IS NOT NULL
   AND COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'NoOfLeadWeeks') IS NULL
BEGIN
    EXEC sp_rename N'dbo.WmsOtsPoAllocationRun.WeeksToInclude', N'NoOfLeadWeeks', N'COLUMN';
END;
