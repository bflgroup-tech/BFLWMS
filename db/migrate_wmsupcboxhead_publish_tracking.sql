/*
 * Adds publish-tracking + PONo columns to Azure dbo.WmsUPCBoxHead for the
 * "Boxes Data Sync to WMSPROD" flow (Azure -> on-prem usa.dbo.upcboxhead).
 *
 * PONo: single ORAPONo of the box (already added by the Counting rework —
 *       this is a no-op if that migration already ran).
 * PublishedTS: DATETIME2 marker stamped when the row has been successfully
 *              pushed to WMSPROD. NULL means "not yet published".
 *              Also indexes it for the incremental "unpublished" scan.
 *
 * Idempotent — safe to re-run.
 */

IF COL_LENGTH('dbo.WmsUPCBoxHead', 'PONo') IS NULL
BEGIN
    ALTER TABLE dbo.WmsUPCBoxHead ADD PONo NVARCHAR(50) NULL;
END;

IF COL_LENGTH('dbo.WmsUPCBoxHead', 'PublishedTS') IS NULL
BEGIN
    ALTER TABLE dbo.WmsUPCBoxHead ADD PublishedTS DATETIME2(0) NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WmsUPCBoxHead_Unpublished'
                 AND object_id = OBJECT_ID('dbo.WmsUPCBoxHead'))
    CREATE INDEX IX_WmsUPCBoxHead_Unpublished
        ON dbo.WmsUPCBoxHead (PublishedTS)
        INCLUDE (BoxNo, Country);
