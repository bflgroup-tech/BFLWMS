/* =============================================================================
   1) Rename Qty -> POQty on the container allocation tables. Historically
      Qty was populated with the row's AllocatedQty (duplicating the
      AllocatedQty column). Renaming to POQty makes its new semantic
      explicit: it holds the item's PO quantity (from usaorgfile_LPM.orgqty).
      AllocatedQty stays as the per-row allocation quantity.

   2) Add Phase2Qty INT NULL to the same tables. Tracks how many pcs of
      AllocatedQty on this row came from the Round-Robin-Rest and Overflow
      passes of the FillSKUMax+RoundRobin run option (i.e. "Phase 2").

   Tables affected:
     - LPMSIM.dbo.WMS_ContAllocationData          (on-prem SIM final)
     - dbo.WMS_ContAllocationData                 (Azure WMS mirror)

   The draft table LPMSIM.dbo.WMS_ContAllocationDraftDetail already has
   both a Qty and a PoQty column; only Phase2Qty is added there — the
   Qty column stays as-is (it maps into the final row via the Confirm
   flow, which is unused today; Process uses SaveFinalDirectAsync).

   Idempotent. Run the LPMSIM block on the on-prem backup DB; run the
   Azure block on the Azure WMS DB.
   ============================================================================= */

/* ---- LPMSIM (on-prem) — MUST USE LPMSIM before sp_rename, because
       sp_rename resolves 'dbo.Table.Column' against the current DB
       context, not the three-part name passed in. ---- */
USE LPMSIM;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'POQty') IS NULL
    EXEC sp_rename 'dbo.WMS_ContAllocationData.Qty', 'POQty', 'COLUMN';
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Phase2Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Phase2Qty INT NULL;
GO
IF COL_LENGTH('dbo.WMS_ContAllocationDraftDetail', 'Phase2Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationDraftDetail ADD Phase2Qty INT NULL;
GO
PRINT 'LPMSIM: WMS_ContAllocationData Qty renamed to POQty; Phase2Qty added on data + draft.';
GO

/* ---- Azure WMS DB ---- */
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'POQty') IS NULL
BEGIN
    EXEC sp_rename 'dbo.WMS_ContAllocationData.Qty', 'POQty', 'COLUMN';
END
GO
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Phase2Qty') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Phase2Qty INT NULL;
GO
PRINT 'Azure WMS: dbo.WMS_ContAllocationData Qty renamed to POQty; Phase2Qty added.';
