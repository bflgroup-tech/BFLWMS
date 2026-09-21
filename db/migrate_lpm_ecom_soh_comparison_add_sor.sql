/*
 * Adds SOR to dbo.LPM_ECOM_SOH_COMPARISON on LPMSIM (on-prem).
 *
 * SOR -> dbo.LPM_ECOM_INCREFF_SOH_NEW (SUM(Quantity) WHERE ItemType = 'SOR'),
 * populated by IncreffMfcsSohCompareService alongside IncreffSOH (also sourced
 * from that table, filtered to ItemType = 'BFL_REGULAR'). Informational only,
 * same as InTransitUAE/InTransitKSA — NOT part of the Variance formula.
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'SOR'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON ADD SOR INT NOT NULL CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_SOR DEFAULT (0);
END;
