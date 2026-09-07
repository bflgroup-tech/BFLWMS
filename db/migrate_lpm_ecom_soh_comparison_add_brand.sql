/*
 * Adds Brand to dbo.LPM_ECOM_SOH_COMPARISON (LPMSIM, on-prem) — sourced from
 * USA.dbo.UPCBarCodes.Vendor, matched by Itemcode. Informational only, no
 * Variance impact.
 *
 * Idempotent: safe to re-run.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'Brand'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD Brand NVARCHAR(255) NULL;
END;
