/*
 * Adds InTransitUAE / InTransitKSA to dbo.LPM_ECOM_SOH_COMPARISON (LPMSIM,
 * on-prem) — quantities in transit to each destination location from
 * RACKS.dbo.MFCS_LOCSTOCK_INT (MFCS_TOLOCID 10007 = UAE, 20002 = KSA), summed
 * by Itemcode.
 *
 * Unlike GateKeeperRejectedSummer/Winter, this source carries no country
 * dimension of its own — the same Itemcode's UAE row and KSA row (when both
 * exist) show the SAME InTransitUAE/InTransitKSA values, matching the raw
 * source queries (grouped by ITEMCODE only, no country filter).
 *
 * Plain columns, not part of the Variance formula (unlike the GS/GW change) —
 * these are informational only, positioned after Variance in the report.
 *
 * Idempotent: safe to re-run.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'InTransitUAE'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD InTransitUAE INT NOT NULL CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_InTransitUAE DEFAULT (0);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'InTransitKSA'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD InTransitKSA INT NOT NULL CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_InTransitKSA DEFAULT (0);
END;
