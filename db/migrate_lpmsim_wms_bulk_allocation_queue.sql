/*
    LPMSIM.dbo.WMS_BulkAllocationQueue
    ----------------------------------
    Input list for the "Run PO Allocation for All" button on the Container
    Allocation page. You load the container numbers; the button walks them.

    ContNo is the only column you have to fill. Country / Warehouse / RunOption
    are per-row OVERRIDES — leave them NULL and the row uses whatever the page's
    inputs are set to when the button is pressed. That keeps the common case a
    one-column insert:

        INSERT INTO dbo.WMS_BulkAllocationQueue (ContNo)
        VALUES ('AELOC6671'), ('AEINT8246'), ('AELOC8263');

    Everything from Status rightwards is written by the run, not by you. Re-running
    resets any row that is not already Success, so a failed batch can be corrected
    and re-run without re-loading the list.

    IsActive lets a row be parked without deleting it — useful when a container
    turns out not to be ready but you want to keep it in the list.

    Run against LPMSIM (the on-prem backup connection's default DB).
*/
IF OBJECT_ID('dbo.WMS_BulkAllocationQueue', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WMS_BulkAllocationQueue
    (
        Id           INT IDENTITY(1,1) NOT NULL,
        ContNo       VARCHAR(50)   NOT NULL,
        Country      VARCHAR(20)   NULL,   -- NULL = use the page's Country
        Warehouse    VARCHAR(50)   NULL,   -- NULL = use the page's Warehouse
        RunOption    VARCHAR(40)   NULL,   -- NULL = use the page's Run Option
        IsActive     BIT           NOT NULL CONSTRAINT DF_WmsBulkAllocQ_IsActive DEFAULT (1),

        -- Written by the run.
        Status       VARCHAR(20)   NULL,   -- Pending | Success | Skipped | Failed
        Message      NVARCHAR(1000) NULL,
        RowsWritten  INT           NULL,
        AllocatedQty INT           NULL,
        BlockedCount INT           NULL,
        StartedTS    DATETIME2(0)  NULL,
        CompletedTS  DATETIME2(0)  NULL,
        RunBy        VARCHAR(100)  NULL,

        CreatedTS    DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WmsBulkAllocQ_CreatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        CONSTRAINT PK_WMS_BulkAllocationQueue PRIMARY KEY CLUSTERED (Id)
    );

    -- One queue entry per container: loading the same ContNo twice would run the
    -- allocation twice, and the second pass would skip itself as already-processed
    -- while reading as a failure in the results.
    CREATE UNIQUE INDEX UX_WmsBulkAllocQ_ContNo
        ON dbo.WMS_BulkAllocationQueue (ContNo);
END
GO
