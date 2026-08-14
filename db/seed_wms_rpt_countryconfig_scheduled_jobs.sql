/*  Seed dbo.WmsRptCountryConfig rows for the batch jobs that are now toggled
    from the Nightly Batches Status page.

    RUN THIS ON THE AZURE WMS DB BEFORE (OR WITH) THE DEPLOY.

    PREREQUISITE: run migrate_wms_rpt_countryconfig_fix_pk.sql first. If this
    table's PK is still the original (Country)-only one, every single-row job
    below collides with WeeklySalesFromGCP's existing Country = '' row and this
    script dies on "Msg 2627 Violation of PRIMARY KEY constraint".

    Two of these jobs previously had NO database gate — they ran whenever their
    timer fired. As of this release both consult WmsRptCountryConfig, and a
    MISSING ROW COUNTS AS INACTIVE. So without this seed, ToteMasterSync and
    BoxesToWmsProd silently stop firing after deploy.

      ToteMasterSync   -> IsActive = 1  (preserves today's behaviour)
      BoxesToWmsProd   -> IsActive = 1  (preserves today's behaviour; the
                                         Scheduler:BoxesToWmsProd:Enabled App
                                         Service flag is still the outer gate)
      OtsWeekly        -> IsActive = 1  (new job: Monday 05:00 GST)
      VolumeGroupWeekly/BFLGROUP
                       -> IsActive = 1  (REQUIRED by OtsWeekly — Generate OTS
                                         throws unless dbo.StoreDivGrade holds
                                         rows generated the same day, and only a
                                         BFLGROUP Volume Group run writes that
                                         table)

    Single-instance jobs use Country = '' by convention (same as
    WeeklySalesFromGCP). Safe to re-run: MERGE leaves an existing row's
    IsActive alone so a deliberate switch-off is not undone.
*/

SET NOCOUNT ON;

DECLARE @seed TABLE (JobName nvarchar(100), Country nvarchar(50), IsActive bit);

INSERT INTO @seed (JobName, Country, IsActive)
VALUES ('OtsWeekly',         '',         1),
       ('ToteMasterSync',    '',         1),
       ('BoxesToWmsProd',    '',         1),
       ('VolumeGroupWeekly', 'BFLGROUP', 1);

MERGE dbo.WmsRptCountryConfig AS t
USING @seed AS s
  ON t.JobName = s.JobName AND t.Country = s.Country
WHEN NOT MATCHED BY TARGET THEN
  INSERT (JobName, Country, IsActive, UpdatedTS, UpdatedBy)
  VALUES (s.JobName, s.Country, s.IsActive,
          DATEADD(hour, 4, SYSUTCDATETIME()), 'migration');

-- Verify.
SELECT JobName, Country, IsActive, UpdatedTS, UpdatedBy
  FROM dbo.WmsRptCountryConfig
 WHERE JobName IN ('OtsWeekly', 'ToteMasterSync', 'BoxesToWmsProd',
                   'VolumeGroupWeekly', 'WeeklySalesFromGCP', 'MissingExcessSnapshot')
 ORDER BY JobName, Country;
