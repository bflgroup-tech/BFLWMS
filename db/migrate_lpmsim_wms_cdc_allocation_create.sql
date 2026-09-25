/* =============================================================================
   CDC Store Allocation — Header / Data / Blocked.

   Run inside LPMSIM (on-prem). Idempotent — re-running is safe.

   A CDC allocation releases stock that the "Future LPMDt -> CDC" hold parked at
   the central DC. It is scoped to one (ContNo, OraPONo), takes its lines from
   racks.dbo.whboxitems where PalletType = 'CD', and runs the same algorithm as
   PO allocation.

   Deliberately SEPARATE tables rather than reusing WMS_Cont_Allocation_Header /
   WMS_ContAllocationData, which are read by six other services (the Azure data
   sync, box building, item encoding, Pass 5, open container, OTS). Sharing them
   would have injected CDC rows into the Azure sync and the building flow on day
   one. The cost of the isolation is that CDC allocations are inert until a
   consumer is deliberately wired up.

   Three departures from the older WMS_Cont_Allocation_* schema, each deliberate:

     1. Timestamps default to GST (DATEADD(hour, 4, SYSUTCDATETIME())), matching
        every newer writer in this codebase, rather than the server-local
        SYSDATETIME() the legacy header uses.
     2. Real foreign keys with ON DELETE CASCADE, so deleting a CDC allocation is
        one statement instead of the legacy three-DELETE dance that has to be
        kept in step at every call site.
     3. String columns fed by SqlBulkCopy are widened. SqlBulkCopy ABORTS the
        whole insert on an over-long value rather than truncating — one reworded
        BlockReason has already broken a save once.
   ============================================================================= */

-- 1) Header — one row per CDC Process run, one run per (ContNo, OraPONo).
IF OBJECT_ID('dbo.WMS_CDC_Allocation_Header','U') IS NULL
CREATE TABLE dbo.WMS_CDC_Allocation_Header (
    BatchNo      INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WCdcAH PRIMARY KEY,
    ContNo       VARCHAR(15)   NOT NULL,
    OraPONo      VARCHAR(25)   NOT NULL,   -- half the business key, hence NOT NULL
    Warehouse    VARCHAR(20)   NULL,
    GenCountry   VARCHAR(20)   NOT NULL,   -- SIM generation country (single)
    Country      VARCHAR(200)  NOT NULL,   -- allocation destinations, comma-separated
    RunOption    VARCHAR(20)   NOT NULL,   -- 'FillMinMinPlusOthers' for now
    -- The flag the spec asks for. 'CDC' here by construction; the column exists
    -- so a query spanning both allocation kinds does not have to infer the type
    -- from which table the row came out of.
    AllocType    VARCHAR(10)   NOT NULL
        CONSTRAINT DF_WCdcAH_AllocType DEFAULT('CDC')
        CONSTRAINT CK_WCdcAH_AllocType CHECK (AllocType IN ('INITIAL','CDC')),
    SourceTable  VARCHAR(100)  NOT NULL
        CONSTRAINT DF_WCdcAH_Src DEFAULT('racks.dbo.whboxitems'),
    PalletType   VARCHAR(10)   NOT NULL
        CONSTRAINT DF_WCdcAH_PalletType DEFAULT('CD'),
    RowCount1    INT           NULL,
    TotalQty     INT           NULL,       -- SUM(AllocatedQty) actually written
    SourceQty    INT           NULL,       -- SUM(whboxitems.Qty) the run started from
    ProcessedTS  DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WCdcAH_PT DEFAULT(DATEADD(hour, 4, SYSUTCDATETIME())),
    ProcessedBy  VARCHAR(100)  NULL,
    ApprovedDt   DATETIME2(0)  NULL,
    ApprovedBy   VARCHAR(100)  NULL
);
GO

/* The re-allocation block: one CDC allocation per (ContNo, OraPONo), ever, until
   it is explicitly deleted.

   Plain UNIQUE rather than filtered on ApprovedDt, because Delete removes the
   header row — so the same action that makes a re-run safe is what unblocks it.

   Enforced in the database, not only in C#, because a UI check is racy: two
   operators on the same PO both pass it before either writes. The service still
   pre-checks for a readable message and catches 2601/2627 on the insert.

   Deliberately NOT an application lock table: migrate_lpmsim_wms_allocation_run_lock.sql
   records that the previous lock wedged and blocked everyone when a run died
   mid-flight. An index has no release path that can fail. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_WCdcAH_Cont_Po'
               AND object_id=OBJECT_ID('dbo.WMS_CDC_Allocation_Header'))
    CREATE UNIQUE INDEX UX_WCdcAH_Cont_Po
      ON dbo.WMS_CDC_Allocation_Header(ContNo, OraPONo);
GO

-- 2) Detail — mirrors WMS_ContAllocationData so the AllocationRow -> SqlBulkCopy
--    mapping is reused verbatim, plus OraPONo promoted to NOT NULL and indexed.
IF OBJECT_ID('dbo.WMS_CDCAllocationData','U') IS NULL
CREATE TABLE dbo.WMS_CDCAllocationData (
    IdNo             BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WCdcAD PRIMARY KEY,
    BatchNo          INT           NOT NULL,
    ContNo           VARCHAR(15)   NOT NULL,
    ORAPONo          VARCHAR(25)   NOT NULL,
    Country          VARCHAR(20)   NOT NULL,
    TrnDate          DATE          NULL,
    Time1            TIME(0)       NULL,
    UPC              VARCHAR(30)   NULL,
    Itemcode         VARCHAR(30)   NULL,
    Barcode          VARCHAR(30)   NULL,
    GroupCode        VARCHAR(10)   NULL,   -- VolumeGroup
    POQty            INT           NULL,   -- the aggregated CD box line qty
    SkuMax           INT           NULL,
    AllocatedQty     INT           NULL,
    PrevAllocatedQty INT           NULL,
    QtyIssue         INT           NULL,
    StoreID          VARCHAR(25)   NULL,
    TcmContno        VARCHAR(15)   NULL,
    Itemname         VARCHAR(150)  NULL,
    BuildingCategory VARCHAR(250)  NULL,
    LPMDt            DATE          NULL,
    Division         VARCHAR(150)  NULL,
    Brand            VARCHAR(150)  NULL,
    DivCode          INT           NULL,
    Department       VARCHAR(150)  NULL,
    Season           VARCHAR(5)    NULL,
    Style            VARCHAR(50)   NULL,
    [Size]           VARCHAR(20)   NULL,
    SalesPrice       DECIMAL(18,2) NULL,
    ResultType       VARCHAR(10)   NULL,
    FinalResult      VARCHAR(100)  NULL,
    Remarks          VARCHAR(50)   NULL,
    OTS              FLOAT         NULL,
    PriorityRank     INT           NULL,
    MnwToday         INT           NULL,
    Phase2Qty        INT           NULL,
    Pass1Qty         INT           NULL,
    Pass2Qty         INT           NULL,
    Pass3Qty         INT           NULL,
    Pass4Qty         INT           NULL,
    RatioSkuMax      INT           NULL,
    AvgOtsPercent    DECIMAL(9,2)  NULL,
    SkuMaxBand       VARCHAR(20)   NULL,
    AvgOtsMin        DECIMAL(9,2)  NULL,
    AvgOtsMax        DECIMAL(9,2)  NULL,
    InitialOtsPct    DECIMAL(9,2)  NULL,
    Soh              INT           NULL,
    RunningOtsQty    INT           NULL,
    OtsQtyToday      INT           NULL,
    TgtEOM           INT           NULL,
    RawSkuMax        INT           NULL,
    MinMinCoverPct   DECIMAL(9,2)  NULL,
    CONSTRAINT FK_WCdcAD_Batch FOREIGN KEY (BatchNo)
        REFERENCES dbo.WMS_CDC_Allocation_Header(BatchNo) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WCdcAD_Batch'
               AND object_id=OBJECT_ID('dbo.WMS_CDCAllocationData'))
    CREATE INDEX IX_WCdcAD_Batch ON dbo.WMS_CDCAllocationData(BatchNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WCdcAD_Cont_Po'
               AND object_id=OBJECT_ID('dbo.WMS_CDCAllocationData'))
    CREATE INDEX IX_WCdcAD_Cont_Po ON dbo.WMS_CDCAllocationData(ContNo, ORAPONo)
        INCLUDE (StoreID, Itemcode, AllocatedQty);
GO

-- 3) Blocked — (item, store) pairs excluded by the access rules.
--    BlockedItemRow has no PO field and is positional, so OraPONo is stamped at
--    save time; the whole run is one PO, so it is a constant.
IF OBJECT_ID('dbo.WMS_CDCAllocationBlocked','U') IS NULL
CREATE TABLE dbo.WMS_CDCAllocationBlocked (
    IdNo        BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WCdcAB PRIMARY KEY,
    BatchNo     INT           NOT NULL,
    ContNo      VARCHAR(15)   NOT NULL,
    ORAPONo     VARCHAR(25)   NOT NULL,
    Country     VARCHAR(20)   NOT NULL,
    RunOption   VARCHAR(20)   NULL,
    ItemCode    VARCHAR(30)   NOT NULL,
    ItemName    VARCHAR(150)  NULL,
    StoreID     VARCHAR(25)   NOT NULL,
    StoreName   VARCHAR(150)  NULL,
    DivCode     INT           NULL,
    Division    VARCHAR(150)  NULL,
    Department  VARCHAR(150)  NULL,
    PoQty       INT           NULL,
    BlockReason VARCHAR(100)  NOT NULL,
    CreatedTS   DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WCdcAB_TS DEFAULT(DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy   VARCHAR(100)  NULL,
    CONSTRAINT FK_WCdcAB_Batch FOREIGN KEY (BatchNo)
        REFERENCES dbo.WMS_CDC_Allocation_Header(BatchNo) ON DELETE CASCADE
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WCdcAB_Batch'
               AND object_id=OBJECT_ID('dbo.WMS_CDCAllocationBlocked'))
    CREATE INDEX IX_WCdcAB_Batch ON dbo.WMS_CDCAllocationBlocked(BatchNo);
GO

PRINT 'CDC allocation tables ready (Header / Data / Blocked, one run per ContNo+OraPONo).';
