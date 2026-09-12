/*
    Azure WMS DB — dbo.WmsRptJobRun
    --------------------------------
    Adds BuildVersion (app Version+LatestPrNumber, e.g. "1.0.622 (PR #607)"),
    stamped by ScheduledJobService.StartRunAsync from the entry assembly at the
    moment each scheduled-job run started.

    Added after a 2026-09-12 incident: a scheduled run's MFCS_SOH numbers matched
    a formula that had been fixed and deployed 17 hours earlier — a stale App
    Service worker (this app runs its job timers in-process per instance) that
    hadn't picked up the redeploy won the job lock and executed old code. Only
    diagnosable at the time via an ad-hoc SQL comparison against current source
    data; this column makes the same class of incident visible directly in the
    Nightly Batches admin page's run log going forward.

    Run against the Azure WMS database. Idempotent — safe to re-run.
*/
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID('dbo.WmsRptJobRun') AND name = 'BuildVersion'
)
BEGIN
    ALTER TABLE dbo.WmsRptJobRun ADD BuildVersion NVARCHAR(50) NULL;
END
GO
