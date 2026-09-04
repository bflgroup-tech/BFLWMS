/*
    LPMSIM.dbo.WMS_BulkAllocationQueue — add BatchNo
    -----------------------------------------------
    Containers are now pushed in batches (from the Pending for Counting page), and
    Bulk PO Allocation can be run against one batch instead of the whole queue.

    The original UNIQUE index was on ContNo alone, which made the queue a single
    permanent worklist: once a container had been queued it could never be queued
    again, so a batch that failed could not be re-pushed. Uniqueness moves to
    (BatchNo, ContNo) — still no duplicate inside a batch, but a container may
    appear in a later batch.

    Rows that predate this change are backfilled to batch 0 so every row belongs to
    a batch and the "which batch?" filter never has a NULL case to special-case.

    Run against LPMSIM. Safe to re-run.
*/
IF COL_LENGTH('dbo.WMS_BulkAllocationQueue', 'BatchNo') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_BulkAllocationQueue ADD BatchNo INT NULL;
END
GO

-- Existing rows become batch 0 ("loaded before batching existed").
UPDATE dbo.WMS_BulkAllocationQueue SET BatchNo = 0 WHERE BatchNo IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID('dbo.WMS_BulkAllocationQueue')
                  AND name = 'BatchNo' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.WMS_BulkAllocationQueue ALTER COLUMN BatchNo INT NOT NULL;
END
GO

IF EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'UX_WmsBulkAllocQ_ContNo'
              AND object_id = OBJECT_ID('dbo.WMS_BulkAllocationQueue'))
BEGIN
    DROP INDEX UX_WmsBulkAllocQ_ContNo ON dbo.WMS_BulkAllocationQueue;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE name = 'UX_WmsBulkAllocQ_Batch_ContNo'
                  AND object_id = OBJECT_ID('dbo.WMS_BulkAllocationQueue'))
BEGIN
    CREATE UNIQUE INDEX UX_WmsBulkAllocQ_Batch_ContNo
        ON dbo.WMS_BulkAllocationQueue (BatchNo, ContNo);
END
GO
