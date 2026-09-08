/*
    LPMSIM.dbo.LPM_DivStoresTurns
    -----------------------------
    Per-store, per-division stock turn. Populated externally.

    This is the source for the "Div/Brand Turn" column on Flagged Allocation
    (Pass 5) and for its store filter: the run takes the selected VG stores in
    the selected countries, averages Turns across them, and keeps the stores
    above that average.

    Shape notes, so a mismatch is caught before data is loaded rather than after:

      - Keyed (Country, StoreID, DivCode) — ONE current row per store/division,
        replaced when turns are refreshed. There is no period in the key, so a
        reload overwrites rather than accumulating. Year1/Month1 are carried as
        plain columns recording which period the figure came from.

      - Turns is DECIMAL(18,4). Turn is a ratio (sales over stock), so an INT
        would silently floor a store at 0.8 turns to 0 and drop it below any
        average.

      - SoldQty / SohQty are optional and only for audit — they let a surprising
        Turns value be checked without going back to the source. Nothing reads
        them.

      - NO Brand column. The table name says Div/Stores, and the Pass 5 filter is
        per (store, division). If turns are actually needed per (division, brand),
        the key has to change before any data is loaded — say so and it is a
        one-line edit here.

    Run against LPMSIM. Safe to re-run.
*/
IF OBJECT_ID('dbo.LPM_DivStoresTurns', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_DivStoresTurns
    (
        Country   VARCHAR(20)    NOT NULL,
        StoreID   VARCHAR(25)    NOT NULL,
        DivCode   INT            NOT NULL,
        Turns     DECIMAL(18,4)  NOT NULL,

        -- Audit only — nothing in the app reads these.
        SoldQty   INT            NULL,
        SohQty    INT            NULL,
        Year1     INT            NULL,
        Month1    INT            NULL,

        UpdatedTS DATETIME2(0)   NOT NULL
            CONSTRAINT DF_LPM_DivStoresTurns_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedBy VARCHAR(100)   NULL,

        CONSTRAINT PK_LPM_DivStoresTurns PRIMARY KEY CLUSTERED (Country, StoreID, DivCode)
    );

    -- The Pass 5 filter reads every store of a division across the selected
    -- countries, so division-first is the order it will actually seek on.
    CREATE INDEX IX_LPM_DivStoresTurns_Div
        ON dbo.LPM_DivStoresTurns (DivCode, Country) INCLUDE (StoreID, Turns);
END
GO
