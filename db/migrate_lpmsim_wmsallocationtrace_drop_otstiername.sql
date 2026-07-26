/*
 * Drops dbo.WmsAllocationTrace.OtsTierName (idempotent).
 *
 * Superseded — instead of stamping a separate "OTS picker's tier name",
 * RawSkuMax / DefaultSkuMax are now populated as the tier value that
 * TierName itself refers to (Pass 1b -> MinMin, Pass 3 -> MinMax,
 * Pass 2 -> OTS picker's tier, Pass 4 -> MinMax). One tier concept per
 * row, no decoding.
 */
IF COL_LENGTH('dbo.WmsAllocationTrace', 'OtsTierName') IS NOT NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace DROP COLUMN OtsTierName;
END;
