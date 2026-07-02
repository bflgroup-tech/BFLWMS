/* =============================================================================
   Add PriorityRank INT NULL to the three tables that carry a Container
   Allocation detail row:
     - LPMSIM.dbo.WMS_ContAllocationDraftDetail   (on-prem SIM draft)
     - LPMSIM.dbo.WMS_ContAllocationData          (on-prem SIM final)
     - dbo.WMS_ContAllocationData                 (Azure WMS mirror)

   Source of truth: LPMSIM.dbo.LPM_EOM_Output.PriorityRank per (StoreId,
   DivCode, Month1, Year1). Lower = higher priority.

   Idempotent. Run the LPMSIM block on the on-prem backup DB; run the Azure
   block on the Azure WMS DB.
   ============================================================================= */

/* ---- LPMSIM (on-prem) ---- */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationDraftDetail', 'PriorityRank') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationDraftDetail ADD PriorityRank INT NULL;
GO
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'PriorityRank') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD PriorityRank INT NULL;
GO
PRINT 'LPMSIM: WMS_ContAllocationDraftDetail + WMS_ContAllocationData have PriorityRank.';
GO

/* ---- Azure WMS DB ---- */
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'PriorityRank') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD PriorityRank INT NULL;
GO
PRINT 'Azure WMS: dbo.WMS_ContAllocationData has PriorityRank.';
