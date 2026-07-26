/*
 * Adds Pass4RatioCap to WMS_ContAllocationData on both LPMSIM (source of
 * SaveDraft/SaveFinal writes) and Azure WMS DB (mirror used by Building
 * and the reports).
 *
 * Pass4RatioCap = the store's OTS-driven tier cap (Min-Max / Ideal-Max /
 * Max-Max) picked at Pass 4 time using LiveOtsPct AFTER Passes 1-3 have
 * refreshed running OTS. It is the denominator used for the ratio-based
 * Pass 4 distribution:
 *     take_i = round(remaining * Pass4RatioCap_i / SUM(Pass4RatioCap))
 * so Pass4Qty / Pass4RatioCap directly shows each store's share.
 *
 * Nullable so pre-existing rows are unaffected. Idempotent.
 */
IF COL_LENGTH('LPMSIM.dbo.WMS_ContAllocationData', 'Pass4RatioCap') IS NULL
    ALTER TABLE LPMSIM.dbo.WMS_ContAllocationData ADD Pass4RatioCap INT NULL;

IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass4RatioCap') IS NULL
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass4RatioCap INT NULL;
