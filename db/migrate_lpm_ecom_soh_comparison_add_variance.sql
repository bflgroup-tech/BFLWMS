/*
 * Adds a computed Variance column to dbo.LPM_ECOM_SOH_COMPARISON on LPMSIM (on-prem).
 *
 * Variance = ABS(IncreffSOH - MFCS_SOH), PERSISTED so it's indexable/queryable
 * without recomputing on every read. Computed columns aren't listed in an
 * explicit INSERT column list, so IncreffMfcsSohCompareService's existing
 * TRUNCATE + INSERT needs no code change — Variance just derives automatically.
 *
 * Idempotent.
 */
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'Variance'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD Variance AS ABS(IncreffSOH - MFCS_SOH) PERSISTED;
END;
