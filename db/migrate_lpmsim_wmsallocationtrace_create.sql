/*
 * Creates dbo.WmsAllocationTrace on LPMSIM.
 *
 * Optional per-run audit trail for FillSKUMax+RoundRobin and
 * FillMinMinPlusOthers. When the operator ticks "Trace Allocation" on
 * the Container Allocation page, the service writes one row here for
 * every Pass touch (a store getting +N units in Pass 1b/2/3/4). That
 * makes it possible to reconstruct WHY a store got its final quantity
 * -- which pass fired, what the LiveOts% + tier cap + SOH were at that
 * moment, how much remaining was left, etc.
 *
 * Rows are keyed by (ContNo, Itemcode, StoreID, Pass) so a store that
 * gets touched by multiple passes for the same item appears as
 * multiple rows here (vs. one aggregated row on WMS_ContAllocationData).
 *
 * Idempotent.
 */
IF OBJECT_ID('dbo.WmsAllocationTrace', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsAllocationTrace (
        TraceId              BIGINT        IDENTITY(1,1) NOT NULL,
        TS                   DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WAT_TS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        ContNo               NVARCHAR(50)  NOT NULL,
        Itemcode             NVARCHAR(50)  NOT NULL,
        StoreID              NVARCHAR(20)  NOT NULL,
        DivCode              INT           NOT NULL,
        Pass                 TINYINT       NOT NULL,      -- 1..4
        SortRank             INT           NOT NULL,      -- position in the pass's sorted store list (0-based)
        VolumeGroup          NVARCHAR(10)  NULL,
        TierName             NVARCHAR(20)  NULL,          -- MinMin / MinMax / IdealMax / MaxMax
        LiveOtsPctBefore     DECIMAL(9,2)  NULL,          -- LiveOts% at moment of decision
        Cap                  INT           NOT NULL,      -- tier cap - SOH (what Pass could give)
        Soh                  INT           NOT NULL,
        CurrentBeforeTake    INT           NOT NULL,      -- units already allocated to this store on this item before this pass
        RemainingBefore      INT           NOT NULL,      -- units left in PO qty before this pass fired
        Take                 INT           NOT NULL,      -- units this pass gave the store (delta)
        RemainingAfter       INT           NOT NULL,      -- units left in PO qty after this pass
        RunningOtsQtyAfter   INT           NOT NULL,      -- store's runningOtsQty at moment of decision
        RunOption            NVARCHAR(60)  NOT NULL,      -- FillSKUMaxRoundRobin | FillMinMinPlusOthers
        RunBy                NVARCHAR(100) NULL,
        SkipReason           NVARCHAR(30)  NULL,          -- NULL=allocated | CapReached | ShareZero
        -- Mirror WMS_ContAllocationData audit columns for JOIN-friendly analysis:
        DefaultSkuMax        INT           NULL,          -- OTS tier picker's effective cap (RawSkuMax - Soh)
        RawSkuMax            INT           NULL,          -- OTS tier picker's raw tier value
        OtsTierName          NVARCHAR(20)  NULL,          -- MinMin / MinMax / IdealMax / MaxMax  (the OTS picker's tier)
        AvgOtsPercent        DECIMAL(9,2)  NULL,
        AvgOtsMin            DECIMAL(9,2)  NULL,
        AvgOtsMax            DECIMAL(9,2)  NULL,
        InitialOtsPct        DECIMAL(9,2)  NULL,          -- OtsPercentToday from WmsOtsPoAllocationRun (pre-allocation)
        CONSTRAINT PK_WmsAllocationTrace PRIMARY KEY (TraceId)
    );
    CREATE INDEX IX_WAT_ContItemStore ON dbo.WmsAllocationTrace (ContNo, Itemcode, StoreID, Pass);
    CREATE INDEX IX_WAT_TS            ON dbo.WmsAllocationTrace (TS DESC);
END;
