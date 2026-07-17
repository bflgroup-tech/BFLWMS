/*
 * Creates dbo.LPM_VolumeGroupRange on LPMSIM (on-prem).
 *
 * Defines the VolumeGroup (Grade) → AvgSalesPct range mapping per
 * operator screenshot. A store's Grade is derived from its AvgSalesPct
 * falling inside one of these ranges. Two grades are special-cased and
 * have IsSpecial = 1:
 *   A → "Above 300% or Top 2 Store"  (numeric floor 300, no ceiling;
 *                                     Top-2 rule can trump the pct check)
 *   Z → "ECOM"                        (ONLINE stores; no AvgSalesPct)
 * For the rest, [AvgSalesPctFrom, AvgSalesPctTo) is treated as
 * From-inclusive / To-exclusive to avoid gap/overlap on boundaries.
 *
 * Idempotent (CREATE + seed both wrapped in existence checks).
 */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_VolumeGroupRange' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_VolumeGroupRange (
        VolumeGroup      NVARCHAR(5)   NOT NULL,   -- A, B, C, D, E, F, G, H, I, Z
        Description      NVARCHAR(200) NOT NULL,   -- "Between 260%-300%" etc.
        AvgSalesPctFrom  DECIMAL(9,4)  NULL,       -- inclusive lower bound (NULL when N/A)
        AvgSalesPctTo    DECIMAL(9,4)  NULL,       -- exclusive upper bound (NULL when open-ended)
        IsSpecial        BIT           NOT NULL
            CONSTRAINT DF_LPM_VolumeGroupRange_IsSpecial DEFAULT (0),
        SortOrder        INT           NOT NULL
            CONSTRAINT DF_LPM_VolumeGroupRange_SortOrder DEFAULT (0),
        UpdatedTS        DATETIME2(0)  NOT NULL
            CONSTRAINT DF_LPM_VolumeGroupRange_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedBy        NVARCHAR(100) NULL,
        CONSTRAINT PK_LPM_VolumeGroupRange PRIMARY KEY (VolumeGroup)
    );
END;

-- Seed: overwrite each grade row idempotently (MERGE pattern).
;WITH src (VolumeGroup, Description, AvgSalesPctFrom, AvgSalesPctTo, IsSpecial, SortOrder) AS (
    SELECT * FROM (VALUES
        ('A', 'Above 300% or Top 2 Store',   CAST(300 AS DECIMAL(9,4)), CAST(NULL AS DECIMAL(9,4)), 1, 1),
        ('B', 'Between 260%-300%',           260, 300, 0, 2),
        ('C', 'Between 210%-260%',           210, 260, 0, 3),
        ('D', 'Between 160%-210%',           160, 210, 0, 4),
        ('E', 'Between 110%-160%',           110, 160, 0, 5),
        ('F', 'Between 90%-110%',             90, 110, 0, 6),
        ('G', 'Between 60%-90%',              60,  90, 0, 7),
        ('H', 'Between 30%-60%',              30,  60, 0, 8),
        ('I', 'Between 0%-30%',                0,  30, 0, 9),
        ('Z', 'ECOM',                       NULL, NULL, 1, 99)
    ) v (VolumeGroup, Description, AvgSalesPctFrom, AvgSalesPctTo, IsSpecial, SortOrder)
)
MERGE dbo.LPM_VolumeGroupRange AS dst
USING src ON dst.VolumeGroup = src.VolumeGroup
WHEN MATCHED THEN UPDATE SET
    Description     = src.Description,
    AvgSalesPctFrom = src.AvgSalesPctFrom,
    AvgSalesPctTo   = src.AvgSalesPctTo,
    IsSpecial       = src.IsSpecial,
    SortOrder       = src.SortOrder,
    UpdatedTS       = DATEADD(hour, 4, SYSUTCDATETIME())
WHEN NOT MATCHED BY TARGET THEN
    INSERT (VolumeGroup, Description, AvgSalesPctFrom, AvgSalesPctTo, IsSpecial, SortOrder)
    VALUES (src.VolumeGroup, src.Description, src.AvgSalesPctFrom, src.AvgSalesPctTo, src.IsSpecial, src.SortOrder);
