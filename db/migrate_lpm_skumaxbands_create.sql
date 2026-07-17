/*
 * Creates dbo.LPM_SkuMaxBands on LPMSIM (on-prem).
 *
 * Stores per-(DivCode, VolumeGroup, PO Qty range) SKU-max caps in four tiers:
 *   MinMin, MinMax, IdealMax, MaxMax.
 *
 * No Country column — same rule applies to all stores across countries.
 * Table is idempotent; safe to re-run.
 *
 * Seed data is in a separate script: db/seed_lpm_skumaxbands_apparel.sql.
 */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_SkuMaxBands' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_SkuMaxBands (
        DivCode     INT           NOT NULL,
        VolumeGroup NVARCHAR(5)   NOT NULL,   -- A, B, C, D, E, F, G, H, I ...
        NoOfStores  INT           NULL,       -- reference only, not used by algorithm
        PoQtyFrom   INT           NOT NULL,   -- 0, 125, 251, 501, 1001 ...
        PoQtyTo     INT           NOT NULL,   -- 125, 250, 500, 1000, 999999 (open-ended top band)
        MinMin      INT           NULL,
        MinMax      INT           NULL,
        IdealMax    INT           NULL,
        MaxMax      INT           NULL,
        IsActive    BIT           NOT NULL
            CONSTRAINT DF_LPM_SkuMaxBands_IsActive DEFAULT (1),
        UpdatedTS   DATETIME2(0)  NOT NULL
            CONSTRAINT DF_LPM_SkuMaxBands_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedBy   NVARCHAR(100) NULL,
        CONSTRAINT PK_LPM_SkuMaxBands PRIMARY KEY (DivCode, VolumeGroup, PoQtyFrom)
    );
END;
