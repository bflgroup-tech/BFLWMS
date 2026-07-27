/*
 * Retires WmsOtsPoAllocationRun + WmsPlanningFlag from Azure WMS DB.
 * Both tables have been moved to LPMSIM; no code path writes here
 * anymore after the corresponding deploy.
 *
 * Run this AFTER the LPMSIM CREATE migrations have been applied AND
 * the app deploy that switches the reads/writes has landed. Running
 * earlier will 500 the OTS PO Allocation page + break Container
 * Allocation Process for FSMRR / FMMPO.
 *
 * Idempotent.
 */
IF OBJECT_ID('dbo.WmsOtsPoAllocationRun', 'U') IS NOT NULL
    DROP TABLE dbo.WmsOtsPoAllocationRun;

IF OBJECT_ID('dbo.WmsPlanningFlag', 'U') IS NOT NULL
    DROP TABLE dbo.WmsPlanningFlag;
