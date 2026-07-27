/*
 * Adds audit columns to dbo.WmsAllocationTrace (all idempotent).
 *
 * SkipReason — why the store got 0 units in this pass:
 *   NULL          — store received Take > 0 (normal allocation)
 *   'CapReached'  — cap - current <= 0 (SOH covers tier, or a prior pass filled it)
 *   'ShareZero'   — Pass 4 proportional share rounded to 0 (store's cap too small vs total)
 *
 * SkuMax / RawSkuMax / AvgOtsPercent / AvgOtsMin / AvgOtsMax / InitialOtsPct —
 * mirror the same-named columns on WMS_ContAllocationData so trace rows line
 * up cleanly with allocation rows for a JOIN-based audit view.
 *
 * SkuMax = OTS tier picker's effective cap (tier - SOH). This differs from
 * the row's `Cap` column, which is the PASS-SPECIFIC cap (Pass 1b uses MinMin,
 * Pass 3 uses MinMax, etc.). Both are useful.
 */
IF COL_LENGTH('dbo.WmsAllocationTrace', 'SkipReason') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD SkipReason NVARCHAR(30) NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'SkuMax') IS NULL
   AND COL_LENGTH('dbo.WmsAllocationTrace', 'DefaultSkuMax') IS NULL
BEGIN
    -- fresh installs get DefaultSkuMax directly; the follow-up rename
    -- migration handles environments that ran this script before that rename.
    ALTER TABLE dbo.WmsAllocationTrace ADD DefaultSkuMax INT NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'RawSkuMax') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD RawSkuMax INT NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'AvgOtsPercent') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD AvgOtsPercent DECIMAL(9,2) NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'AvgOtsMin') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD AvgOtsMin DECIMAL(9,2) NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'AvgOtsMax') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD AvgOtsMax DECIMAL(9,2) NULL;
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'InitialOtsPct') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD InitialOtsPct DECIMAL(9,2) NULL;
END;
