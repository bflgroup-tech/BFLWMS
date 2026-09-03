/*
    LPMSIM.dbo.DC_STORE_SOH_ALLOCATION
    ----------------------------------
    Output of the "Allocate DC SOH to Stores" button on the CDC Box Allocation
    page (menu CDC_BOX_ALLOCATION).

    One row per (Itemcode, StoreID): how much of the UAE DC eligible SOH for
    that SKU is notionally assigned to that store, decided by the store's
    Volume Group + the LPM_SkuMaxBands tier for the SKU's total eligible DC qty.

    The table holds ONE run at a time — the allocate step wipes it and re-inserts,
    so the Process step always reads a single coherent snapshot rather than a mix
    of LPM scopes. RunTS / RunBy / LpmScope / CountryScope record which run
    produced the rows currently in it, so the page can show "last allocated at …
    for LPMs …" and Process can refuse to run against a stale/absent snapshot.

    Everything except AllocatedSoh is audit: DcQty is the band basis, OtsPercent /
    AvgOtsPercent / TierName / SkuMaxTier / StoreSoh are the inputs that produced
    the cap, so a surprising number can be traced without re-running.

    Run against LPMSIM (the on-prem backup connection's default DB).
*/
IF OBJECT_ID('dbo.DC_STORE_SOH_ALLOCATION', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DC_STORE_SOH_ALLOCATION
    (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        RunTS         DATETIME2(0)  NOT NULL,
        RunBy         VARCHAR(100)  NULL,
        LpmScope      VARCHAR(1000) NULL,   -- comma-joined LPM labels this run covered
        CountryScope  VARCHAR(500)  NULL,   -- comma-joined allocation countries
        Itemcode      VARCHAR(50)   NOT NULL,
        DivCode       INT           NOT NULL,
        Country       VARCHAR(20)   NOT NULL,
        StoreID       VARCHAR(25)   NOT NULL,
        VolumeGroup   VARCHAR(10)   NULL,
        DcQty         INT           NOT NULL,   -- total eligible DC qty for the SKU (band basis)
        OtsPercent    DECIMAL(18,2) NULL,       -- store's OtsPercentToday from WmsOtsCdcAllocationRun
        AvgOtsPercent DECIMAL(18,2) NULL,       -- division average of positive OTS% for this item
        TierName      VARCHAR(20)   NULL,       -- MinMin / MinMax / IdealMax / MaxMax
        SkuMaxTier    INT           NOT NULL CONSTRAINT DF_DCSSA_SkuMaxTier DEFAULT (0),
        StoreSoh      INT           NOT NULL CONSTRAINT DF_DCSSA_StoreSoh   DEFAULT (0),
        AllocatedSoh  INT           NOT NULL,
        CONSTRAINT PK_DC_STORE_SOH_ALLOCATION PRIMARY KEY CLUSTERED (Id)
    );

    -- One row per SKU/Store is the contract the Process step relies on.
    CREATE UNIQUE INDEX UX_DCSSA_Item_Store
        ON dbo.DC_STORE_SOH_ALLOCATION (Itemcode, StoreID);

    -- Process reads it as "SOH for this (Store, Item)" — same shape as the
    -- LPM_locstock lookup it replaces.
    CREATE INDEX IX_DCSSA_Store_Item
        ON dbo.DC_STORE_SOH_ALLOCATION (StoreID, Itemcode) INCLUDE (AllocatedSoh);
END
GO
