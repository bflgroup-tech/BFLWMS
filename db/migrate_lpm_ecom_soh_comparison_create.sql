/*
 * Creates dbo.LPM_ECOM_SOH_COMPARISON on LPMSIM (on-prem).
 *
 * Item-level comparison of two ECOM SOH sources, rebuilt in full on every
 * refresh (TRUNCATE + INSERT):
 *   IncreffSOH -> dbo.LPM_ECOM_INCREFF_SOH   (BigQuery INCREFF feed)
 *   MFCS_SOH   -> RACKS.dbo.lpm_locstock     (StoreID = 'ONLINE'/'ONLINEKSA')
 * One row per (Country, Itemcode) present in EITHER source (FULL OUTER JOIN) —
 * the missing side is written as 0, not NULL.
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_ECOM_SOH_COMPARISON' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_ECOM_SOH_COMPARISON (
        Country    NVARCHAR(20) NOT NULL,
        Itemcode   NVARCHAR(30) NOT NULL,
        IncreffSOH INT          NOT NULL,
        MFCS_SOH   INT          NOT NULL,
        CreateTS   DATETIME2(0) NOT NULL
            CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        CONSTRAINT PK_LPM_ECOM_SOH_COMPARISON PRIMARY KEY (Country, Itemcode)
    );
END;
