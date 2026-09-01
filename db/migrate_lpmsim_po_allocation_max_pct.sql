/*
 * Creates dbo.LPM_POAllocationMaxPct on LPMSIM (idempotent) and seeds ECOM at 15%.
 *
 * RUN ON LPMSIM BEFORE DEPLOYING.
 *
 * Caps how much of a container's PO Qty a country may take in Container
 * Allocation. Today only ECOM is capped, but the table is keyed by Country so
 * another market can be limited without a code change.
 *
 * POAllocationMaxPct is a PERCENT (15 = 15%), not a fraction — the column name
 * says Pct and an operator editing this by hand will type 15.
 *
 * The cap is PER DIVISION within a container, not per item line and not per
 * container. Each division gets its own budget:
 *     FLOOR(that division's PO Qty in the container * POAllocationMaxPct / 100)
 * so a division's allowance can only be spent by its own items. Within a
 * division it is still first-come: early item lines can consume the whole
 * budget and later lines of the same division then get none.
 *
 * NO ROW = NO CAP. A country absent from this table allocates unrestricted,
 * exactly as before the cap existed. That is deliberate: a missing row must not
 * silently zero a country's allocation.
 */

IF OBJECT_ID('dbo.LPM_POAllocationMaxPct', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LPM_POAllocationMaxPct (
        Country             VARCHAR(20)   NOT NULL,
        POAllocationMaxPct  DECIMAL(5,2)  NOT NULL,
        Remarks             NVARCHAR(300) NULL,
        UpdatedTS           DATETIME2(0)  NULL,
        UpdatedBy           VARCHAR(100)  NULL,
        CONSTRAINT PK_LPM_POAllocationMaxPct PRIMARY KEY (Country),
        CONSTRAINT CK_LPM_POAllocationMaxPct_Pct CHECK (POAllocationMaxPct >= 0 AND POAllocationMaxPct <= 100)
    );
    PRINT 'Created dbo.LPM_POAllocationMaxPct';
END
ELSE
    PRINT 'dbo.LPM_POAllocationMaxPct already exists';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.LPM_POAllocationMaxPct WHERE Country = 'ECOM')
    INSERT INTO dbo.LPM_POAllocationMaxPct (Country, POAllocationMaxPct, Remarks, UpdatedTS, UpdatedBy)
    VALUES ('ECOM', 15.00, 'ECOM takes at most 15% of a container''s PO Qty.',
            DATEADD(hour, 4, SYSUTCDATETIME()), 'migration');
GO

SELECT Country, POAllocationMaxPct FROM dbo.LPM_POAllocationMaxPct ORDER BY Country;
PRINT 'dbo.LPM_POAllocationMaxPct ready.';
