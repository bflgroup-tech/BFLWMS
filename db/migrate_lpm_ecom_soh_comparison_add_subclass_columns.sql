/*
 * Adds Division/Department/Class/Subclass/Family to dbo.LPM_ECOM_SOH_COMPARISON
 * on LPMSIM (on-prem), denormalized from DATAREPORTING.dbo.vUPC_SUBCLASS at
 * write time by IncreffMfcsSohCompareService — so the ECOM Stock Variance Report
 * no longer has to LEFT JOIN a 20M-row view at read time.
 *
 * Widths match vUPC_SUBCLASS's own columns (NVARCHAR(255) each).
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'Division'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD Division   NVARCHAR(255) NULL,
            Department NVARCHAR(255) NULL,
            Class      NVARCHAR(255) NULL,
            Subclass   NVARCHAR(255) NULL,
            Family     NVARCHAR(255) NULL;
END;
