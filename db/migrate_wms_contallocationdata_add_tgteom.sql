/*
 * Adds TgtEOM INT NULL to LPMSIM.dbo.WMS_ContAllocationData.
 *
 * Populated only by the FillSKUMax+RoundRobin run option — value copied
 * from WmsOtsPoAllocationRun.TgtEOM for the row's (StoreID, DivCode). The
 * other run options (FillSKUMax, RoundRobin) leave it NULL.
 *
 * Idempotent.
 */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'TgtEOM') IS NULL
BEGIN
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD TgtEOM INT NULL;
END;
