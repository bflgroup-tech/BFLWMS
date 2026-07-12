/*
 * Adds Pass1Qty..Pass4Qty + AvgOtsPercent columns to Azure
 * dbo.WMS_ContAllocationData for the new OTS-run-based
 * FillSKUMax + RoundRobin allocation algorithm.
 *
 * Each pass qty is the piece count that came out of that pass for the
 * (StoreID, Itemcode) row:
 *   Pass1Qty  — OTS% >= AvgOTS%
 *   Pass2Qty  — 0 < OTS% < AvgOTS%
 *   Pass3Qty  — OTS% <= 0, round-robin
 *   Pass4Qty  — uncapped RR fallback across all eligible stores
 *
 * AvgOtsPercent = the per-Division AVG(OtsPercentToday WHERE > 0) that
 * was used at the moment this item was allocated. Same value on every
 * row of a given item; refreshed between items.
 *
 * All columns nullable so pre-existing rows are unaffected. Idempotent.
 */

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass1Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass1Qty INT NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass2Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass2Qty INT NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass3Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass3Qty INT NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass4Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass4Qty INT NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'AvgOtsPercent') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD AvgOtsPercent DECIMAL(10,2) NULL;
