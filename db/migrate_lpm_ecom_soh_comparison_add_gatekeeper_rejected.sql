/*
 * Adds GateKeeperRejectedSummer / GateKeeperRejectedWinter to
 * dbo.LPM_ECOM_SOH_COMPARISON (LPMSIM, on-prem) — UAE-only quantities from
 * RACKS.dbo.WHBoxItems (PalletType 'GS'/'GW' respectively), summed by Itemcode.
 * KSA rows get 0 for both (RACKS.dbo.WHBoxItems has no country split — GS/GW
 * pallets are a UAE-only concept).
 *
 * Redefines Variance to net these out of IncreffSOH:
 *   was:  MFCS_SOH - IncreffSOH
 *   now:  MFCS_SOH - (IncreffSOH + GateKeeperRejectedSummer + GateKeeperRejectedWinter)
 * A computed column's formula can't be ALTERed in place; drop and re-add.
 *
 * Idempotent: safe to re-run.
 */
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'GateKeeperRejectedSummer'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD GateKeeperRejectedSummer INT NOT NULL CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_GKRS DEFAULT (0);
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'GateKeeperRejectedWinter'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
        ADD GateKeeperRejectedWinter INT NOT NULL CONSTRAINT DF_LPM_ECOM_SOH_COMPARISON_GKRW DEFAULT (0);
END;

IF EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.LPM_ECOM_SOH_COMPARISON') AND name = 'Variance'
)
BEGIN
    ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON DROP COLUMN Variance;
END;

ALTER TABLE dbo.LPM_ECOM_SOH_COMPARISON
    ADD Variance AS (MFCS_SOH - (IncreffSOH + GateKeeperRejectedSummer + GateKeeperRejectedWinter)) PERSISTED;
