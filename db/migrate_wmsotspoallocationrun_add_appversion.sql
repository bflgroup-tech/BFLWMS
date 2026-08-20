/*
 * Adds dbo.WmsOtsPoAllocationRun.AppVersion (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — Generate bulk-copies by column name, so
 * persisting fails outright against a table without it.
 *
 * Holds the build that produced the row (e.g. '1.0.458'), read from the running
 * Wms.Web assembly. Both writers stamp it — the Generate button and the 07:00
 * scheduled job — because they share OtsPoAllocationService.GenerateAndPersistAsync.
 *
 * Why: "did the scheduled run pick up yesterday's deploy?" came up twice and was
 * only answerable by back-solving arithmetic from the stored figures — e.g.
 * deducing from TargetWeek 36 vs 37 that a run had PR #445 but not #447. This
 * turns that into a lookup:
 *
 *     SELECT DISTINCT OTSDate, RunTS, RunBy, AppVersion
 *       FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
 *      WHERE [Year] = YEAR(GETDATE()) AND [Month] = MONTH(GETDATE())
 *      ORDER BY OTSDate DESC, RunTS DESC;
 *
 * A scheduled row showing an older AppVersion than the deployed build means the
 * App Service process had not restarted, which is an infrastructure problem
 * rather than a code one — and the column makes that visible immediately.
 *
 * Existing rows keep NULL; the build that wrote them is not recoverable.
 */
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'AppVersion') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD AppVersion VARCHAR(30) NULL;
END;
GO

-- Verify.
SELECT COL_LENGTH('dbo.WmsOtsPoAllocationRun','AppVersion') AS AppVersion_Len;
