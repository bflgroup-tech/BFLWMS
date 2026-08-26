/* =============================================================================
   Adds the WMS-Prod-DB enrichment columns to Azure dbo.WMS_ContAllocationData
   (idempotent). RUN ON THE AZURE WMS DB BEFORE DEPLOYING — the Azure sync
   bulk-copies by column name, so the copy fails outright against a table
   without these.

   Background: the "Azure WMS DB" destination on Container Allocation Data Sync
   used to be a straight column-for-column mirror of the LPMSIM allocation rows.
   It now follows the same logic as the WMS-Prod-DB destination — one row per
   piece, enriched from bfldata.dbo.DataSettings and RFSalesPrice — and these
   six columns are the ones online.dbo.PhotoCheckingResult had that Azure did
   not.

   Column notes:
     PrintFlag  'Y'/'N' — DataSettings.POAllocation_PrintFlag for the store.
     RfidFlag   'Y'/'N' — DataSettings RFID flag for the store.
     Company            — DataSettings.Company.
     ShopCode           — DataSettings.ShopCode.
     OrPrice    FLOAT   — [Dataname].dbo.RFSalesPrice for (store, item). FLOAT
                          rather than DECIMAL to match PhotoCheckingResult, so
                          the two destinations hold the identical value.
                          0 (not NULL) when the store does not print stickers.
     RefNo              — the shop's BFLDATA..RFIDTransfer TrfNo for today.

   NOT added, deliberately:
     SalesPrice as text. PhotoCheckingResult stores "AED 12.00" in a varchar(30);
     Azure's SalesPrice is DECIMAL(18,4) and is already read as a number by
     Manual Counting. The numeric price lands in OrPrice instead; re-formatting
     for display is a presentation concern.
   ============================================================================= */

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'PrintFlag') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD PrintFlag VARCHAR(1) NULL;
GO

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'RfidFlag') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD RfidFlag VARCHAR(1) NULL;
GO

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Company') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Company VARCHAR(100) NULL;
GO

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'ShopCode') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD ShopCode VARCHAR(20) NULL;
GO

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'OrPrice') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD OrPrice FLOAT NULL;
GO

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'RefNo') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD RefNo VARCHAR(50) NULL;
GO

PRINT 'Azure dbo.WMS_ContAllocationData: WMS-Prod-DB enrichment columns ready.';
