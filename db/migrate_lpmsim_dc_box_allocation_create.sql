/*
    LPMSIM.dbo.DC_BOX_ALLOCATION
    ----------------------------
    Output of the "Process" button on the CDC Box Allocation page — the whole-box
    shipment plan.

    One row per (BoxNo, Itemcode, StoreID). Every row of a given BoxNo carries the
    SAME Country: a box is shipped intact, so it goes to exactly one country, and
    only the units inside it may split across stores within that country. That is
    the constraint the whole Process step exists to honour.

    Qty splits into two audit columns:
      WithinTarget - units that fitted the store's DC_STORE_SOH_ALLOCATION figure
      OverTarget   - units placed beyond it because the box had to go somewhere
                     whole. Non-zero here is expected, not a fault; it is the
                     visible cost of box integrity, and worth watching because a
                     large total means the box sizes and the store targets
                     disagree.

    Holds ONE run at a time, like DC_STORE_SOH_ALLOCATION: the Process step wipes
    and re-inserts, so the plan always reflects a single LPM/country scope rather
    than a mix of runs.

    Run against LPMSIM (the on-prem backup connection's default DB).
*/
IF OBJECT_ID('dbo.DC_BOX_ALLOCATION', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DC_BOX_ALLOCATION
    (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        RunTS        DATETIME2(0)  NOT NULL,
        RunBy        VARCHAR(100)  NULL,
        LpmScope     VARCHAR(1000) NULL,
        CountryScope VARCHAR(500)  NULL,
        BoxNo        VARCHAR(50)   NOT NULL,
        LPMDt        DATE          NULL,
        Itemcode     VARCHAR(50)   NOT NULL,
        DivCode      INT           NULL,
        Country      VARCHAR(20)   NOT NULL,
        StoreID      VARCHAR(25)   NOT NULL,
        Qty          INT           NOT NULL,
        WithinTarget INT           NOT NULL CONSTRAINT DF_DCBA_WithinTarget DEFAULT (0),
        OverTarget   INT           NOT NULL CONSTRAINT DF_DCBA_OverTarget   DEFAULT (0),
        CONSTRAINT PK_DC_BOX_ALLOCATION PRIMARY KEY CLUSTERED (Id)
    );

    -- The box -> country integrity check reads by box; the store pick lists read
    -- by destination.
    CREATE INDEX IX_DCBA_Box     ON dbo.DC_BOX_ALLOCATION (BoxNo) INCLUDE (Country, StoreID, Qty);
    CREATE INDEX IX_DCBA_Country ON dbo.DC_BOX_ALLOCATION (Country, StoreID, Itemcode) INCLUDE (Qty);
END
GO

/*
    Boxes the run could not place, with the reason. Kept separate from the plan so
    "what shipped" and "what did not" never have to be told apart by a NULL.
*/
IF OBJECT_ID('dbo.DC_BOX_ALLOCATION_UNPLACED', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DC_BOX_ALLOCATION_UNPLACED
    (
        Id       BIGINT IDENTITY(1,1) NOT NULL,
        RunTS    DATETIME2(0)  NOT NULL,
        BoxNo    VARCHAR(50)   NOT NULL,
        LPMDt    DATE          NULL,
        Qty      INT           NOT NULL,
        Items    INT           NOT NULL,
        Reason   NVARCHAR(400) NULL,
        CONSTRAINT PK_DC_BOX_ALLOCATION_UNPLACED PRIMARY KEY CLUSTERED (Id)
    );
END
GO
