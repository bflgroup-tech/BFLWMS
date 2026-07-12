/*
 * Adds OtsQtyToday INT NULL to LPMSIM.dbo.WMS_ContAllocationData.
 *
 * Populated at Process time from WmsOtsPoAllocationRun.OtsQtyToday for
 * the row's (StoreID, DivCode). This is the initial value (not the running
 * remaining) — so all detail rows for the same (Store, Div) in one batch
 * carry the same value; that's what the "OTS Qty Today" report column shows.
 *
 * Idempotent.
 */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'OtsQtyToday') IS NULL
BEGIN
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD OtsQtyToday INT NULL;
END;
