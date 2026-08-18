/*
 * Adds an Origin column to WMS_ContAllocationDataSync_Log so Recent Activity
 * can distinguish 'Manual' clicks from 'Scheduled' background-service runs.
 *
 * RUN ON THE AZURE WMS DB.
 *
 * Backfills existing rows to 'Manual' (they were all triggered from the UI).
 * Idempotent — safe to re-run.
 *
 * NOTE ON THE BATCH SPLIT (the GO below is load-bearing):
 * The first version of this script had the UPDATE in the same batch as the
 * ALTER TABLE. SQL Server compiles a batch before executing any of it, so the
 * UPDATE failed name resolution on a column the ALTER had not created yet:
 *
 *     Msg 207, Level 16, State 1  Invalid column name 'Origin'.
 *
 * The whole batch aborts, so the ALTER never ran either and the script looked
 * like it had done nothing. WITH VALUES now does the backfill inline (a plain
 * DEFAULT on a NULLable column does NOT populate existing rows), and the
 * separate batch afterwards is only a safety net for a table where the column
 * already existed with NULLs in it.
 */

IF COL_LENGTH('dbo.WMS_ContAllocationDataSync_Log', 'Origin') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationDataSync_Log
        ADD Origin NVARCHAR(10) NULL
        CONSTRAINT DF_WMS_ContAllocationDataSync_Log_Origin DEFAULT('Manual') WITH VALUES;
END;
GO

-- Separate batch: Origin is resolvable now. Covers a table where the column
-- pre-existed without the backfill having been applied.
UPDATE dbo.WMS_ContAllocationDataSync_Log
   SET Origin = 'Manual'
 WHERE Origin IS NULL;
GO

-- Verify.
SELECT COL_LENGTH('dbo.WMS_ContAllocationDataSync_Log', 'Origin') AS OriginColumnLength;

SELECT Origin, COUNT(*) AS Rows
  FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
 GROUP BY Origin
 ORDER BY Origin;
