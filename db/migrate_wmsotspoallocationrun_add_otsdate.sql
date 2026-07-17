/*
 * Adds OTSDate DATE column (no time) to Azure dbo.WmsOtsPoAllocationRun
 * so multiple daily snapshots per (Month, Year) can coexist. Generate
 * DELETEs rows for TODAY only, preserving prior days.
 *
 * Backfills existing rows: OTSDate = CAST(RunTS AS DATE).
 * Idempotent.
 */

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'OTSDate') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD OTSDate DATE NULL;
END;

UPDATE dbo.WmsOtsPoAllocationRun
   SET OTSDate = CAST(RunTS AS DATE)
 WHERE OTSDate IS NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
     WHERE name = N'IX_WmsOtsPoAllocationRun_MonthYearOTSDate'
       AND object_id = OBJECT_ID(N'dbo.WmsOtsPoAllocationRun'))
    CREATE INDEX IX_WmsOtsPoAllocationRun_MonthYearOTSDate
        ON dbo.WmsOtsPoAllocationRun ([Year], [Month], OTSDate, Country, DivCode)
        INCLUDE (StoreID, StoreName, Division, VolumeGroup, PriorityRank,
                 TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh,
                 CountingWIP, OtsQtyToday, OtsPercentToday);
