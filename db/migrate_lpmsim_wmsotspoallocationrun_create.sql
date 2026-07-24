/*
 * Creates dbo.WmsOtsPoAllocationRun on LPMSIM.
 *
 * The Azure copy is being retired — Generate now persists here and every
 * downstream reader (OTS PO Allocation page + Container Allocation
 * FSMRR/FMMPO) reads from here instead of Azure. Fresh start; no data
 * migration (users re-Generate).
 *
 * Schema mirrors the retiring Azure table exactly, including the
 * later-added columns (PrevMonthEOM, WkReduction, CurrentEOW, OTSDate)
 * so the code path is drop-in.
 *
 * Idempotent.
 */

IF OBJECT_ID(N'dbo.WmsOtsPoAllocationRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsOtsPoAllocationRun
    (
        RunId            INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_WmsOtsPoAllocationRun PRIMARY KEY,
        RunTS            DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_TS       DEFAULT (SYSDATETIME()),
        RunBy            NVARCHAR(100) NULL,
        [Month]          INT           NOT NULL,
        [Year]           INT           NOT NULL,
        Country          NVARCHAR(20)  NOT NULL,
        StoreID          NVARCHAR(15)  NOT NULL,
        StoreName        NVARCHAR(200) NULL,
        DivCode          INT           NOT NULL,
        Division         NVARCHAR(100) NULL,
        VolumeGroup      NVARCHAR(20)  NULL,
        PriorityRank     INT           NULL,
        TgtEOM           INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_TgtEOM    DEFAULT (0),
        SOHToday         INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_SOH       DEFAULT (0),
        WeeksToInclude   INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_Weeks     DEFAULT (1),
        WeekSales        INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_WeekSales DEFAULT (0),
        InTransit        INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_InTransit DEFAULT (0),
        Ex2DcSoh         INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_Ex2Dc     DEFAULT (0),
        CountingWIP      INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_WIP       DEFAULT (0),
        OtsQtyToday      INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_OtsQty    DEFAULT (0),
        OtsPercentToday  DECIMAL(10,2) NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_OtsPct    DEFAULT (0),
        PrevMonthEOM     INT           NULL,
        WkReduction      DECIMAL(18,4) NULL,
        CurrentEOW       INT           NULL,
        OTSDate          DATE          NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_WmsOtsPoAllocationRun_MonthYear'
                 AND object_id = OBJECT_ID(N'dbo.WmsOtsPoAllocationRun'))
    CREATE INDEX IX_WmsOtsPoAllocationRun_MonthYear
        ON dbo.WmsOtsPoAllocationRun ([Year], [Month], Country, DivCode)
        INCLUDE (StoreID, StoreName, Division, VolumeGroup, PriorityRank,
                 TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh,
                 CountingWIP, OtsQtyToday, OtsPercentToday,
                 PrevMonthEOM, WkReduction, CurrentEOW, OTSDate);
