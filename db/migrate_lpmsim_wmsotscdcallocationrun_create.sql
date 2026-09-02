/*
 * Creates dbo.WmsOtsCdcAllocationRun on LPMSIM (idempotent) — the persistence
 * target for the new "OTS for CDC Box Allocation" page.
 *
 * RUN ON LPMSIM BEFORE DEPLOYING.
 *
 * Structurally identical to dbo.WmsOtsPoAllocationRun, deliberately as a
 * SEPARATE TABLE rather than a RunType discriminator on the existing one:
 *
 *   - Both pages key rows by (Year, Month, OTSDate, Country, StoreID, DivCode)
 *     and both DELETE-then-insert that key on Generate. Sharing one table means
 *     whichever page ran last wins, and PO allocation would silently read CDC
 *     numbers.
 *   - A discriminator would have to be added to the existing table and to every
 *     read on it, which is exactly the modification of the PO pages the brief
 *     rules out.
 *
 * The two differ only in what feeds them: this one always treats UAE DC SOH as
 * ZERO, so its Ex2 DC SOH carries a country's own export-warehouse stock alone
 * and its UaeDcSoh column is always 0. Every other column, and the OTS formula
 * itself, is shared code (OtsPoAllocationService with a different OtsRunTarget).
 */

IF OBJECT_ID('dbo.WmsOtsCdcAllocationRun', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsOtsCdcAllocationRun (
        RunTS            DATETIME2(0)  NOT NULL,
        RunBy            VARCHAR(100)  NULL,
        [Month]          INT           NOT NULL,
        [Year]           INT           NOT NULL,
        OTSDate          DATE          NULL,
        Country          VARCHAR(20)   NOT NULL,
        StoreID          VARCHAR(25)   NOT NULL,
        StoreName        VARCHAR(150)  NULL,
        DivCode          INT           NOT NULL,
        Division         VARCHAR(150)  NULL,
        VolumeGroup      VARCHAR(10)   NULL,
        PriorityRank     INT           NULL,
        TgtEOMMonth      VARCHAR(20)   NULL,
        TgtEOM           INT           NULL,
        SOHToday         INT           NULL,
        NoOfLeadWeeks    INT           NULL,
        WeekSales        INT           NULL,
        LeadIntransit    INT           NULL,
        LeadDCSOH        INT           NULL,
        InTransit        INT           NULL,
        Ex2DcSoh         INT           NULL,
        UaeDcSoh         INT           NULL,
        CountingWIP      INT           NULL,
        OtsQtyToday      INT           NULL,
        OtsPercentToday  DECIMAL(18,4) NULL,
        PrevEOMMonth     VARCHAR(20)   NULL,
        PrevMonthEOM     INT           NULL,
        DivisorWeeks     INT           NULL,
        WeekAdjustment   DECIMAL(18,4) NULL,
        CurrentWeek      INT           NULL,
        TargetWeek       INT           NULL,
        WeeksMultiplier  INT           NULL,
        CurrentEOW       INT           NULL,
        AppVersion       VARCHAR(50)   NULL
    );
    PRINT 'Created dbo.WmsOtsCdcAllocationRun';
END
ELSE
    PRINT 'dbo.WmsOtsCdcAllocationRun already exists';
GO

/* Load reads by (Year, Month, OTSDate) then filters Country — same access path
   the PO table is indexed for. */
IF OBJECT_ID('dbo.WmsOtsCdcAllocationRun', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID('dbo.WmsOtsCdcAllocationRun')
                      AND name = 'IX_WmsOtsCdcAllocationRun_YearMonthDate')
    CREATE INDEX IX_WmsOtsCdcAllocationRun_YearMonthDate
        ON dbo.WmsOtsCdcAllocationRun ([Year], [Month], OTSDate)
        INCLUDE (Country, StoreID, DivCode);
GO

PRINT 'dbo.WmsOtsCdcAllocationRun ready.';
