/*
 * Adds analysis columns to WMS_ContAllocationData (both LPMSIM and Azure
 * WMS mirror) so operators can trace every allocation row back to the
 * exact tier + OTS state that produced it.
 *
 *   SkuMaxBand    — tier the picker landed on: 'MinMin' | 'MinMax' |
 *                   'IdealMax' | 'MaxMax'. Reflects the LAST pass that
 *                   wrote to this row (each pass may re-pick as LiveOts
 *                   refreshes).
 *   AvgOtsMin     — AvgOts - OTSBandPct (lower edge of IdealMax band).
 *                   Same for every row of a given item.
 *   AvgOtsMax     — AvgOts + OTSBandPct (upper edge of IdealMax band).
 *   InitialOtsQty — OtsQtyToday from WmsOtsPoAllocationRun at the start
 *                   of Process (before any decrement). Same across all
 *                   items for the same (Store, Div) in one Process run.
 *   Soh           — per-(Store, Item) SOH from racks.LPM_locstock used
 *                   in the cap = tier - SOH calc.
 *   RunningOtsQty — runningOtsQty AT the moment this row was written.
 *                   Useful with CurrentEOW to verify the row's stored
 *                   OTS = RunningOtsQty / CurrentEOW * 100.
 *
 * All nullable so pre-existing rows are unaffected. Idempotent.
 */

-- LPMSIM (source of Save)
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'SkuMaxBand') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD SkuMaxBand NVARCHAR(20) NULL;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'AvgOtsMin') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD AvgOtsMin DECIMAL(9,2) NULL;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'AvgOtsMax') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD AvgOtsMax DECIMAL(9,2) NULL;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'InitialOtsQty') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD InitialOtsQty INT NULL;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'Soh') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD Soh INT NULL;
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'RunningOtsQty') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD RunningOtsQty INT NULL;

-- Azure WMS (mirror)
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'SkuMaxBand') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD SkuMaxBand NVARCHAR(20) NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'AvgOtsMin') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD AvgOtsMin DECIMAL(9,2) NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'AvgOtsMax') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD AvgOtsMax DECIMAL(9,2) NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'InitialOtsQty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD InitialOtsQty INT NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Soh') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Soh INT NULL;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'RunningOtsQty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD RunningOtsQty INT NULL;
