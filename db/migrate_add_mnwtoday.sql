/* =============================================================================
   Add MnwToday INT NULL to the three tables that carry a Container Allocation
   detail row:
     - LPMSIM.dbo.WMS_ContAllocationDraftDetail   (on-prem SIM draft)
     - LPMSIM.dbo.WMS_ContAllocationData          (on-prem SIM final)
     - dbo.WMS_ContAllocationData                 (Azure WMS mirror)

   Source of truth: LPMSIM.dbo.OTSOutput.Mnwtoday, latest row per
   (StoreID, DivCode) by OTSDate DESC.

   MerchNeedMonth is intentionally NOT dropped — it stays in the tables for
   history / algorithm sort, just isn't displayed on the UI anymore.

   Idempotent. Run the LPMSIM block on the on-prem backup DB; run the Azure
   block on the Azure WMS DB.
   ============================================================================= */

/* ---- LPMSIM (on-prem) ---- */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationDraftDetail', 'MnwToday') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationDraftDetail ADD MnwToday INT NULL;
GO
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'MnwToday') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD MnwToday INT NULL;
GO
PRINT 'LPMSIM: WMS_ContAllocationDraftDetail + WMS_ContAllocationData have MnwToday.';
GO

/* ---- Azure WMS DB ---- */
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'MnwToday') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD MnwToday INT NULL;
GO
PRINT 'Azure WMS: dbo.WMS_ContAllocationData has MnwToday.';
