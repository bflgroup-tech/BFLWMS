/*
 * Adds RawSkuMax INT NULL to LPMSIM.dbo.WMS_ContAllocationData.
 *
 * Populated only by the FillSKUMax+RoundRobin run option — value copied
 * from LPM_SKUMaxRule.SKUMax when the (Country, DivCode, GroupCode,
 * comboPoQty) lookup found a matching band; 0 when no band matched.
 * The existing SkuMax column continues to record the effective cap
 * (max(0, RawSkuMax - SOHToday)) so both values are visible side-by-side.
 *
 * Idempotent.
 */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'RawSkuMax') IS NULL
BEGIN
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD RawSkuMax INT NULL;
END;
