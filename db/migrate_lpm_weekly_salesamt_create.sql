/*
 * Creates dbo.LPM_Weekly_SalesAmt on LPMSIM (on-prem).
 *
 * Per-(StoreID, DivCode, Year, Month, Week) weekly sales amount roll-up.
 * The monthly rollup already exists elsewhere; this one keeps the same
 * grain with a Week axis added so downstream logic can source per-week
 * revenue directly without slicing month rows.
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_Weekly_SalesAmt' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_Weekly_SalesAmt (
        StoreID   NVARCHAR(50)  NOT NULL,
        DivCode   INT           NOT NULL,
        Year1     INT           NOT NULL,
        Month1    INT           NOT NULL,
        Week      INT           NOT NULL,
        SalesAmt  DECIMAL(18,2) NULL,
        CreateTS  DATETIME2(0)  NOT NULL
            CONSTRAINT DF_LPM_Weekly_SalesAmt_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        CONSTRAINT PK_LPM_Weekly_SalesAmt PRIMARY KEY (StoreID, DivCode, Year1, Month1, Week)
    );
    CREATE INDEX IX_LPM_Weekly_SalesAmt_YearMonth ON dbo.LPM_Weekly_SalesAmt (Year1, Month1)
        INCLUDE (DivCode, Week, SalesAmt);
END;
