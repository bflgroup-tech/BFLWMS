/*
 * Adds DivCode to dbo.LPM_POAllocationMaxPct so the cap can be set per
 * (Country, Division) instead of per country only (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING.
 *
 * DivCode = 0 means "every division in this country" — the country-wide
 * default. A row for a specific DivCode overrides it for that division only.
 * That keeps the ECOM 15% row already seeded working untouched: it becomes the
 * default, and you add exceptions only where a division needs a different
 * number.
 *
 * Resolution order at allocation time:
 *      1. exact (Country, DivCode)
 *      2. (Country, 0)          -- country-wide default
 *      3. no row at all         -- UNCAPPED
 *
 * 0 rather than NULL as the wildcard because it can sit in the primary key;
 * a nullable PK column is not allowed, and a filtered unique index would make
 * the lookup harder to read for no gain.
 *
 * Example — ECOM capped at 15% everywhere except Sports Apparel at 25%:
 *      Country  DivCode  POAllocationMaxPct
 *      ECOM     0        15.00
 *      ECOM     413      25.00
 */

IF COL_LENGTH('dbo.LPM_POAllocationMaxPct', 'DivCode') IS NULL
BEGIN
    -- Existing rows are country-wide by definition, so they take the 0 wildcard.
    ALTER TABLE dbo.LPM_POAllocationMaxPct
        ADD DivCode INT NOT NULL CONSTRAINT DF_LPMPOAllocMaxPct_DivCode DEFAULT (0);
    PRINT 'Added DivCode';
END
ELSE
    PRINT 'DivCode already present';
GO

-- Repoint the primary key at (Country, DivCode). Dropping first because the
-- original PK is on Country alone and would reject a second row for a country.
IF EXISTS (SELECT 1 FROM sys.key_constraints
            WHERE name = 'PK_LPM_POAllocationMaxPct'
              AND parent_object_id = OBJECT_ID('dbo.LPM_POAllocationMaxPct'))
   AND NOT EXISTS (SELECT 1
                     FROM sys.index_columns ic
                     JOIN sys.indexes ix ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id
                     JOIN sys.columns  c  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ix.name = 'PK_LPM_POAllocationMaxPct'
                      AND c.name  = 'DivCode')
BEGIN
    ALTER TABLE dbo.LPM_POAllocationMaxPct DROP CONSTRAINT PK_LPM_POAllocationMaxPct;
    ALTER TABLE dbo.LPM_POAllocationMaxPct
        ADD CONSTRAINT PK_LPM_POAllocationMaxPct PRIMARY KEY (Country, DivCode);
    PRINT 'Primary key repointed to (Country, DivCode)';
END
ELSE
    PRINT 'Primary key already covers DivCode (or table predates this script)';
GO

SELECT Country, DivCode, POAllocationMaxPct
  FROM dbo.LPM_POAllocationMaxPct
 ORDER BY Country, DivCode;
PRINT 'dbo.LPM_POAllocationMaxPct is now (Country, DivCode) keyed.';
