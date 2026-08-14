/*  Repair dbo.WmsRptCountryConfig's primary key so it is (JobName, Country).

    RUN THIS BEFORE seed_wms_rpt_countryconfig_scheduled_jobs.sql.

    Why this exists
    ---------------
    migrate_wms_rpt_countryconfig_add_jobname.sql widens the PK from (Country)
    to (JobName, Country) — but it does that INSIDE its
    `IF NOT EXISTS (JobName column)` guard. On any database where the JobName
    column arrived some other way, the guard short-circuits and the PK swap is
    skipped permanently, leaving the original auto-named PK on (Country) alone.

    Symptom: inserting a second job's single-row config (Country = '') fails with

        Msg 2627 ... Violation of PRIMARY KEY constraint 'PK__WmsRptCo__...'

    because WeeklySalesFromGCP already holds the one allowed Country = '' row.
    The same failure hits the page's Active toggles at runtime, not just the seed.

    This script checks the PK's actual column list rather than the JobName
    column, so it repairs that state. Idempotent — a correct PK is left alone.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- JobName must exist and be NOT NULL before it can take part in a PK.
IF NOT EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID('dbo.WmsRptCountryConfig') AND name = 'JobName')
BEGIN
    ALTER TABLE dbo.WmsRptCountryConfig ADD JobName NVARCHAR(100) NULL;
END;
GO

UPDATE dbo.WmsRptCountryConfig
   SET JobName = 'MissingExcessSnapshot'
 WHERE JobName IS NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
              AND name = 'JobName' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.WmsRptCountryConfig ALTER COLUMN JobName NVARCHAR(100) NOT NULL;
END;
GO

-- Is the PK already exactly (JobName, Country)? Compare the real key columns.
DECLARE @pkName   NVARCHAR(200),
        @pkCols   NVARCHAR(400),
        @sql      NVARCHAR(MAX);

SELECT @pkName = kc.name
  FROM sys.key_constraints kc
 WHERE kc.parent_object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
   AND kc.type = 'PK';

SELECT @pkCols = STUFF((
        SELECT ',' + c.name
          FROM sys.key_constraints kc
          JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id
                                   AND ic.index_id  = kc.unique_index_id
          JOIN sys.columns c        ON c.object_id  = ic.object_id
                                   AND c.column_id  = ic.column_id
         WHERE kc.parent_object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
           AND kc.type = 'PK'
         ORDER BY ic.key_ordinal
         FOR XML PATH('')), 1, 1, '');

PRINT 'Current PK: ' + ISNULL(@pkName, '(none)') + ' on (' + ISNULL(@pkCols, '') + ')';

IF @pkCols IS NULL OR @pkCols <> 'JobName,Country'
BEGIN
    -- Widening (Country) -> (JobName, Country) can never create a duplicate:
    -- the narrower key already guaranteed uniqueness. Guard anyway so the
    -- script fails loudly rather than half-applied if the table is unexpected.
    IF EXISTS (SELECT 1 FROM dbo.WmsRptCountryConfig
                GROUP BY JobName, Country HAVING COUNT(*) > 1)
    BEGIN
        RAISERROR('Duplicate (JobName, Country) rows exist — resolve them before rebuilding the PK.', 16, 1);
        RETURN;
    END;

    BEGIN TRAN;

    IF @pkName IS NOT NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.WmsRptCountryConfig DROP CONSTRAINT ' + QUOTENAME(@pkName) + N';';
        PRINT @sql;
        EXEC sp_executesql @sql;
    END;

    ALTER TABLE dbo.WmsRptCountryConfig
        ADD CONSTRAINT PK_WmsRptCountryConfig PRIMARY KEY (JobName, Country);

    COMMIT;
    PRINT 'PK rebuilt as PK_WmsRptCountryConfig (JobName, Country).';
END
ELSE
BEGIN
    PRINT 'PK already correct — nothing to do.';
END;
GO

-- Verify.
SELECT kc.name AS PkName, ic.key_ordinal, c.name AS PkColumn
  FROM sys.key_constraints kc
  JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id
                           AND ic.index_id  = kc.unique_index_id
  JOIN sys.columns c        ON c.object_id  = ic.object_id
                           AND c.column_id  = ic.column_id
 WHERE kc.parent_object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
   AND kc.type = 'PK'
 ORDER BY ic.key_ordinal;
