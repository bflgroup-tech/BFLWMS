/*
 * dbo.WmsOtsPoAllocationRun on Azure bfl-wms.
 *
 * Persists the "OTS for PO Allocation" report so the Generate step (heavy
 * cross-DB compute) runs once and Load reads from here — filtered by
 * country + divisions — in a single Azure round trip.
 *
 * One row per (Month, Year, Country, StoreID, DivCode). Regenerating for
 * a (Month, Year) DELETEs the existing rows and re-INSERTs.
 *
 * Run inside the Azure WMS DB. Idempotent.
 */

IF OBJECT_ID(N'dbo.WmsOtsPoAllocationRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsOtsPoAllocationRun
    (
        RunId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WmsOtsPoAllocationRun PRIMARY KEY,
        RunTS            DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_TS DEFAULT (SYSDATETIME()),
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
        TgtEOM           INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_TgtEOM DEFAULT (0),
        SOHToday         INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_SOH DEFAULT (0),
        WeeksToInclude   INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_Weeks DEFAULT (1),
        WeekSales        INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_WeekSales DEFAULT (0),
        InTransit        INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_InTransit DEFAULT (0),
        Ex2DcSoh         INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_Ex2Dc DEFAULT (0),
        CountingWIP      INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_WIP DEFAULT (0),
        OtsQtyToday      INT           NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_OtsQty DEFAULT (0),
        OtsPercentToday  DECIMAL(10,2) NOT NULL CONSTRAINT DF_WmsOtsPoAllocationRun_OtsPct DEFAULT (0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_WmsOtsPoAllocationRun_MonthYear'
                 AND object_id = OBJECT_ID(N'dbo.WmsOtsPoAllocationRun'))
    CREATE INDEX IX_WmsOtsPoAllocationRun_MonthYear
        ON dbo.WmsOtsPoAllocationRun ([Year], [Month], Country, DivCode)
        INCLUDE (StoreID, StoreName, Division, VolumeGroup, PriorityRank,
                 TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh,
                 CountingWIP, OtsQtyToday, OtsPercentToday);
