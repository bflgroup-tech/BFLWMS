/*
 * Adds SalesQty to dbo.LPM_Weekly_SalesAmt (LPMSIM, per-country on-prem).
 *
 * Populated by WeeklySalesFromGcpService from BigQuery's
 * cdm_silver.it_sales_qty.Soldqty, alongside the existing SalesAmt
 * (from NetSalesExVAT). Nullable so historic rows without a qty stay
 * valid until the next sync touches them.
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.LPM_Weekly_SalesAmt')
      AND name = 'SalesQty'
)
BEGIN
    ALTER TABLE dbo.LPM_Weekly_SalesAmt
        ADD SalesQty INT NULL;
END;
