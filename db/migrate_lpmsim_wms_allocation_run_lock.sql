/*
    LPMSIM.dbo.WMS_AllocationRunLock
    --------------------------------
    One row, one lock. Held for the duration of an allocation run so a single
    container Process and a Bulk PO Allocation run cannot overlap — in either
    direction.

    Why a table and not sp_getapplock: an application lock lives on the SQL
    session, and a run opens and closes dozens of short-lived connections. The
    lock has to outlive any one of them.

    Why not a static in the app: App Service can run more than one instance, and
    a process-local lock would let two instances run concurrently while each
    believed it was alone.

    HeartbeatTS is what makes it safe to hold for hours without wedging the app
    forever. A run refreshes it as it works; if the app dies mid-run the row goes
    stale and the next acquirer takes it over. StaleMinutes is deliberately well
    above a single container's worst observed runtime (~5 min) so a slow-but-alive
    run is never stolen from.

    Run against LPMSIM (the on-prem backup connection's default DB).
*/
IF OBJECT_ID('dbo.WMS_AllocationRunLock', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WMS_AllocationRunLock
    (
        LockName    VARCHAR(50)   NOT NULL,
        RunKind     VARCHAR(20)   NULL,   -- 'Single' | 'Bulk' | NULL when free
        Scope       NVARCHAR(200) NULL,   -- ContNo, or "batch #3" / "all batches"
        HeldBy      VARCHAR(100)  NULL,
        AcquiredTS  DATETIME2(0)  NULL,
        HeartbeatTS DATETIME2(0)  NULL,
        CONSTRAINT PK_WMS_AllocationRunLock PRIMARY KEY CLUSTERED (LockName)
    );
END
GO

-- The single lock row. Present and free from the start, so acquiring is an
-- UPDATE with a WHERE guard rather than an INSERT racing another INSERT.
IF NOT EXISTS (SELECT 1 FROM dbo.WMS_AllocationRunLock WHERE LockName = 'PO_ALLOCATION')
    INSERT INTO dbo.WMS_AllocationRunLock (LockName) VALUES ('PO_ALLOCATION');
GO
