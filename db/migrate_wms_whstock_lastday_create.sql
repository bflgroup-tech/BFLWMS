/*
 * Creates dbo.WMS_WHSTOCK_LASTDAY on LPMSIM (on-prem).
 *
 * Mirrors the BigQuery source mvp-data-bi.cdm_silver.wh_stock_last_day
 * (schema as given: Country/Warehouse/PalletCategory/LastDayOfMonth/Division/
 * Season/Qty/SKUCount/BoxCount/PalletCount/Created_ts), column-for-column, so
 * the future GCP-pull service can bulk-copy the feed straight into a staging
 * table of this same shape before MERGE-upserting.
 *
 * Grain is one row per (Country, Warehouse, PalletCategory, LastDayOfMonth,
 * Division, Season) -- same reasoning as dbo.LPM_Weekly_SalesAmt's PK: every
 * non-measure column together forms the natural key, Qty/SKUCount/BoxCount/
 * PalletCount are the measures.
 *
 * Created_ts is the source's own column (BigQuery declares it DATE, not a
 * timestamp, despite the name) -- kept as-is for a faithful mirror. CreateTS/
 * UpdatedTS below are this table's OWN load-audit columns (stamped by the
 * upsert, not sourced from BigQuery) -- deliberately capitalized differently
 * from Created_ts to keep the two visually distinct in queries.
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WMS_WHSTOCK_LASTDAY' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.WMS_WHSTOCK_LASTDAY (
        Country        NVARCHAR(50)  NOT NULL,
        Warehouse      NVARCHAR(50)  NOT NULL,
        PalletCategory NVARCHAR(50)  NOT NULL,
        LastDayOfMonth DATE          NOT NULL,
        Division       NVARCHAR(100) NOT NULL,
        Season         NVARCHAR(20)  NOT NULL,
        Qty            INT           NULL,
        SKUCount       INT           NULL,
        BoxCount       INT           NULL,
        PalletCount    INT           NULL,
        Created_ts     DATE          NULL,
        CreateTS       DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WMS_WHSTOCK_LASTDAY_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedTS      DATETIME2(0)  NULL,
        CONSTRAINT PK_WMS_WHSTOCK_LASTDAY PRIMARY KEY (Country, Warehouse, PalletCategory, LastDayOfMonth, Division, Season)
    );
    CREATE INDEX IX_WMS_WHSTOCK_LASTDAY_LastDayOfMonth ON dbo.WMS_WHSTOCK_LASTDAY (LastDayOfMonth)
        INCLUDE (Country, Warehouse, Qty, SKUCount, BoxCount, PalletCount);
END;
