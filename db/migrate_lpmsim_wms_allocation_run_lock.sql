/*
    LPMSIM.dbo.WMS_AllocationRunLock — OBSOLETE, kept for the record.
    ----------------------------------------------------------------
    This table backed a mutual exclusion between a single-container Process and a
    Bulk PO Allocation run. The feature was removed: a run killed mid-flight
    (deploy, app restart, crash) never reached its release, so the lock wedged and
    blocked everyone until a 20-minute stale window expired. That cost more than
    the overlap it prevented.

    The table is now unused by the application. It can be dropped:

        DROP TABLE dbo.WMS_AllocationRunLock;

    The original CREATE is left below, commented, in case the idea is revisited —
    if it is, it needs a release path that does not depend on the app surviving.
*/

-- IF OBJECT_ID('dbo.WMS_AllocationRunLock', 'U') IS NULL
-- BEGIN
--     CREATE TABLE dbo.WMS_AllocationRunLock
--     (
--         LockName    VARCHAR(50)   NOT NULL,
--         RunKind     VARCHAR(20)   NULL,
--         Scope       NVARCHAR(200) NULL,
--         HeldBy      VARCHAR(100)  NULL,
--         AcquiredTS  DATETIME2(0)  NULL,
--         HeartbeatTS DATETIME2(0)  NULL,
--         CONSTRAINT PK_WMS_AllocationRunLock PRIMARY KEY CLUSTERED (LockName)
--     );
--     INSERT INTO dbo.WMS_AllocationRunLock (LockName) VALUES ('PO_ALLOCATION');
-- END
