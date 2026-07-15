/*
 * Adds PrevMonthEOM / WkReduction / CurrentEOW columns to
 * dbo.WmsOtsPoAllocationRun on Azure WMS DB.
 *
 * Semantics:
 *   PrevMonthEOM = TargetEOM from LPM_EOM_Output for the previous (Month, Year)
 *                  (per StoreID + DivCode). 0 when the store had no prior-month row.
 *   WkReduction  = (PrevMonthEOM - TgtEOM) / weeksInCurrentMonth. 0 when
 *                  PrevMonthEOM = 0.
 *   CurrentEOW   = PrevMonthEOM - (WkReduction * weeksElapsedInMonthSoFar).
 *                  Falls back to TgtEOM when PrevMonthEOM = 0 so the OTS math
 *                  matches today's behaviour for stores without history.
 *
 * OTS formula: otsQty  = CurrentEOW + WeekSales - SOH - InTransit - Ex2DcSoh - CountingWIP
 *              otsPct  = otsQty / CurrentEOW * 100 (0 when CurrentEOW <= 0)
 *
 * Idempotent.
 */

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'PrevMonthEOM') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD PrevMonthEOM INT NULL;

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'WkReduction') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD WkReduction DECIMAL(18,4) NULL;

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'CurrentEOW') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD CurrentEOW INT NULL;
