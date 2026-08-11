/*
 * Adds JobName to dbo.WmsRptCountryConfig (Azure SQL WMS) so the same
 * per-country Active-toggle table can host more than one nightly/weekly
 * batch process (MissingExcessSnapshot today, WeeklySalesFromGCP next).
 *
 * Existing rows are backfilled to 'MissingExcessSnapshot' — the only job
 * that used this table before JobName existed. PK moves from (Country) to
 * (JobName, Country) so each job gets its own per-country Active row.
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
      AND name = 'JobName'
)
BEGIN
    ALTER TABLE dbo.WmsRptCountryConfig
        ADD JobName NVARCHAR(100) NULL;

    UPDATE dbo.WmsRptCountryConfig SET JobName = 'MissingExcessSnapshot' WHERE JobName IS NULL;

    ALTER TABLE dbo.WmsRptCountryConfig
        ALTER COLUMN JobName NVARCHAR(100) NOT NULL;

    -- Original CREATE TABLE declared PRIMARY KEY inline, so SQL Server
    -- auto-named the constraint (PK__WmsRptCo__...) — look it up rather
    -- than assuming a name.
    DECLARE @pk NVARCHAR(200) = (
        SELECT kc.name FROM sys.key_constraints kc
        WHERE kc.parent_object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
          AND kc.type = 'PK');
    DECLARE @sql NVARCHAR(400) = N'ALTER TABLE dbo.WmsRptCountryConfig DROP CONSTRAINT ' + QUOTENAME(@pk) + N';';
    EXEC sp_executesql @sql;

    ALTER TABLE dbo.WmsRptCountryConfig
        ADD CONSTRAINT PK_WmsRptCountryConfig PRIMARY KEY (JobName, Country);
END;
