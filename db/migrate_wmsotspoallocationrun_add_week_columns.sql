/*
 * Adds dbo.WmsOtsPoAllocationRun.CurrentWeek, .TargetWeek and .WeeksMultiplier
 * (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — Generate bulk-copies by column name, so
 * persisting fails outright against a table without these.
 *
 * What they hold:
 *   CurrentWeek     = latest wk in LPM_OTS_Output (the run's anchor week).
 *                     Same value on every row.
 *   TargetWeek      = CurrentWeek + NoOfLeadWeeks - 1, per country. The month
 *                     this week falls in is the Tgt EOM Month.
 *   WeeksMultiplier = TargetWeek - the last week of the month BEFORE the target
 *                     month, i.e. how many weeks INTO the target month the
 *                     target week sits.
 *
 * WeeksMultiplier REPLACES NoOfLeadWeeks as the CurrentEOW multiplier:
 *
 *     WeekAdjustment = (TgtEOM - PrevMonthEOM) / DivisorWeeks
 *     CurrentEOW     = PrevMonthEOM + WeekAdjustment * WeeksMultiplier
 *
 * The two differ whenever the lead horizon does not land on a month boundary,
 * so CurrentEOW — and therefore OTS — shifts on the first run after deploy.
 *
 * Existing rows keep NULL: they were computed with the NoOfLeadWeeks multiplier,
 * and backfilling a value would imply they reconcile against the new formula.
 */
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'CurrentWeek') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD CurrentWeek INT NULL;
END;
GO

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'TargetWeek') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD TargetWeek INT NULL;
END;
GO

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'WeeksMultiplier') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD WeeksMultiplier INT NULL;
END;
GO

-- Verify.
SELECT COL_LENGTH('dbo.WmsOtsPoAllocationRun','CurrentWeek')     AS CurrentWeek_Len,
       COL_LENGTH('dbo.WmsOtsPoAllocationRun','TargetWeek')      AS TargetWeek_Len,
       COL_LENGTH('dbo.WmsOtsPoAllocationRun','WeeksMultiplier') AS WeeksMultiplier_Len;
