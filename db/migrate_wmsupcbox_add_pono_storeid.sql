/*
 * Phase 2 migration for the Counting rework.
 *
 *   WmsUPCBoxHead: add PONo (the single ORAPONo saved at check-out; validated
 *                  uniform across all box items before saving)
 *   WmsUPCBoxDet:  add StoreId (the target store the piece was routed to)
 *                  and ToteID (mirror of the parent box's tote — redundant
 *                  with WmsUPCBoxHead.ToteID but stamped on each Det row so
 *                  downstream consumers can query without joining Head).
 *
 * All columns are NULLable so pre-existing rows are unaffected. New writes
 * from BuildingService.CheckoutBoxAsync will populate them.
 *
 * Idempotent — safe to re-run.
 */

IF COL_LENGTH('dbo.WmsUPCBoxHead', 'PONo') IS NULL
BEGIN
    ALTER TABLE dbo.WmsUPCBoxHead ADD PONo NVARCHAR(50) NULL;
END;

IF COL_LENGTH('dbo.WmsUPCBoxDet', 'StoreId') IS NULL
BEGIN
    ALTER TABLE dbo.WmsUPCBoxDet ADD StoreId NVARCHAR(15) NULL;
END;

IF COL_LENGTH('dbo.WmsUPCBoxDet', 'ToteID') IS NULL
BEGIN
    ALTER TABLE dbo.WmsUPCBoxDet ADD ToteID NVARCHAR(50) NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WmsUPCBoxDet_CountryStore'
                 AND object_id = OBJECT_ID('dbo.WmsUPCBoxDet'))
    CREATE INDEX IX_WmsUPCBoxDet_CountryStore
        ON dbo.WmsUPCBoxDet (Country, StoreId);
