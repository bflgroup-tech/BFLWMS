/*
 * Adds an Origin column to WMS_ContAllocationDataSync_Log so Recent Activity
 * can distinguish 'Manual' clicks from 'Scheduled' background-service runs.
 *
 * Backfills existing rows to 'Manual' (they were all triggered from the UI).
 * Idempotent — safe to re-run.
 */

IF COL_LENGTH('dbo.WMS_ContAllocationDataSync_Log', 'Origin') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationDataSync_Log
        ADD Origin NVARCHAR(10) NULL
        CONSTRAINT DF_WMS_ContAllocationDataSync_Log_Origin DEFAULT('Manual');

    UPDATE dbo.WMS_ContAllocationDataSync_Log SET Origin = 'Manual' WHERE Origin IS NULL;
END;
