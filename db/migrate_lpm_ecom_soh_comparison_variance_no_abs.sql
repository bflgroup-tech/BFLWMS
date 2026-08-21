/*
 * Redefines the Variance computed column on dbo.LPM_ECOM_SOH_COMPARISON (LPMSIM,
 * on-prem): was ABS(IncreffSOH - MFCS_SOH), now MFCS_SOH - IncreffSOH (signed —
 * negative when IncreffSOH > MFCS_SOH).
 *
 * A computed column's formula can't be ALTERed in place; drop and re-add.
 * Idempotent: safe to re-run (drops if present, then (re-)adds with the new formula).
 */
SET QUOTED_IDENTIFIER ON;

IF EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'Variance'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON DROP COLUMN Variance;
END;

ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
    ADD Variance AS (MFCS_SOH - IncreffSOH) PERSISTED;
