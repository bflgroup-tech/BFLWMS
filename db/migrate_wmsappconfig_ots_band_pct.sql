/*
 * Seeds the OTSBandPct entry in dbo.WmsAppConfig (Azure/WMS DB) used by
 * Fill SKUMAX + RR to derive AvgOTS +/- band boundaries for tier picking
 * from LPM_SkuMaxBands. Default is 10 (percentage points).
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM dbo.WmsAppConfig WHERE ConfigKey = 'OTSBandPct')
BEGIN
    INSERT INTO dbo.WmsAppConfig (ConfigKey, ConfigValue)
    VALUES ('OTSBandPct', '10');
END;
