/*
 * Creates dbo.LPM_ECOM_INCREFF_SOH_NEW on LPMSIM (on-prem).
 *
 * Parallel/validation destination for IncreffSohFromGcpNewService, which reads
 * a newer BigQuery Silver-tier source (Ecom_Silver.Increff_Item_Level_SOH) —
 * one combined table across channels/countries, with Item_Type/Bin Status
 * granularity the old per-country Bronze-tier tables (Ecom_Bronze.INCREFF_
 * {Country}_SOH, read by IncreffSohFromGcpService into dbo.LPM_ECOM_INCREFF_SOH)
 * don't have. Deliberately a SEPARATE table/job so this can be validated
 * against the existing job's output without touching the live ECOM Stock
 * Variance Report, which still reads dbo.LPM_ECOM_INCREFF_SOH.
 *
 * One row per (Country, Itemcode, ItemType, BinStatus) for the latest pulled
 * CalenderDate — current-snapshot semantics only (no history), same as
 * dbo.LPM_ECOM_INCREFF_SOH: refresh deletes each country's existing rows and
 * bulk-inserts the fresh set. No PK/indexes, matching that table's heap shape
 * (a bare pre-created table, per IncreffSohFromGcpService's own doc comment).
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LPM_ECOM_INCREFF_SOH_NEW' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.LPM_ECOM_INCREFF_SOH_NEW (
        Country      VARCHAR(50)  NULL,
        Itemcode     VARCHAR(30)  NULL,
        ItemType     VARCHAR(100) NULL,
        BinStatus    VARCHAR(100) NULL,
        Quantity     INT          NULL,
        CalenderDate DATE         NULL,
        CreateTS     DATETIME     NULL
    );
END;
