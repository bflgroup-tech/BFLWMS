/*
 * Swaps InitialOtsQty for InitialOtsPct on WMS_ContAllocationData
 * (LPMSIM + Azure WMS mirror).
 *
 *   InitialOtsPct = OtsPercentToday from WmsOtsPoAllocationRun at the
 *                   start of Process (static per (Store, Div) run row).
 *                   Same numerator/denominator basis as the OTS PO
 *                   Allocation report, so numbers line up 1:1.
 *
 * The OtsQty value was raw units; the % form is easier to eyeball
 * against the report's Avg Sales % column without dividing by CurrentEOW.
 *
 * Idempotent: guards on both DROP and ADD.
 */

-- LPMSIM (source of Save)
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'InitialOtsQty') IS NOT NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData DROP COLUMN InitialOtsQty;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'InitialOtsPct') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD InitialOtsPct DECIMAL(9,2) NULL;

-- Azure WMS (mirror)
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'InitialOtsQty') IS NOT NULL
    ALTER TABLE dbo.WMS_ContAllocationData DROP COLUMN InitialOtsQty;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'InitialOtsPct') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD InitialOtsPct DECIMAL(9,2) NULL;
