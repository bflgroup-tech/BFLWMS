/*
 * Adds dbo.WmsOtsPoAllocationRun.TgtEOMMonth + .PrevEOMMonth (idempotent).
 *
 * TgtEOM is now sourced from the calendar month that the store's country's
 * "last week of sales included" falls into — e.g. Qatar with N=3, current
 * fiscal week 30, considers weeks 30/31/32; week 32 lies in August, so
 * TgtEOM = August's LPM_EOM_Output.TargetEOM.
 *
 * TgtEOMMonth / PrevEOMMonth store the "MMM-yyyy" labels of those months
 * so operators can see at a glance which month's target was used, and
 * which month's target the prior-month EOM came from.
 *
 * PrevMonthEOM continues to hold TargetEOM for (TgtEOMMonth - 1); its
 * label is PrevEOMMonth.
 */
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'TgtEOMMonth') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD TgtEOMMonth NVARCHAR(20) NULL;
END;
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'PrevEOMMonth') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD PrevEOMMonth NVARCHAR(20) NULL;
END;
