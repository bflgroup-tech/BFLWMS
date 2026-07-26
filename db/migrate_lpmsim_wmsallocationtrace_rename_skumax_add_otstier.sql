/*
 * Renames dbo.WmsAllocationTrace.SkuMax to DefaultSkuMax and adds
 * dbo.WmsAllocationTrace.OtsTierName (both idempotent).
 *
 * Rationale — a trace row currently carries two "tier" values that mean
 * different things:
 *   * TierName        = the PASS'S chosen tier (Pass 1b always MinMin;
 *                       Pass 3 always MinMax; Pass 2 uses the OTS picker)
 *   * SkuMax / RawSkuMax = the OTS PICKER'S chosen tier (based on LiveOts%
 *                       vs Avg +/- Band), regardless of which pass fired
 * That naming clash was confusing. This migration:
 *   1. Renames SkuMax -> DefaultSkuMax so its role as the OTS picker's
 *      effective ceiling (RawSkuMax - Soh) is explicit
 *   2. Adds OtsTierName so the OTS picker's tier name is stamped alongside
 *      RawSkuMax / DefaultSkuMax on every row
 * After the migration a row reads:
 *   TierName='MinMin', Cap=0, Take=0, OtsTierName='IdealMax', RawSkuMax=3, DefaultSkuMax=2, Soh=1
 * which unambiguously says "Pass 1b's MinMin cap was 0 because SOH already
 * covered it; the OTS picker allowed up to IdealMax=3 -> 2 after SOH."
 */
IF COL_LENGTH('dbo.WmsAllocationTrace', 'SkuMax') IS NOT NULL
   AND COL_LENGTH('dbo.WmsAllocationTrace', 'DefaultSkuMax') IS NULL
BEGIN
    EXEC sp_rename N'dbo.WmsAllocationTrace.SkuMax', N'DefaultSkuMax', N'COLUMN';
END;
IF COL_LENGTH('dbo.WmsAllocationTrace', 'OtsTierName') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD OtsTierName NVARCHAR(20) NULL;
END;
