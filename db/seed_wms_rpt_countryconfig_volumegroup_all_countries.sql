/*  Activate VolumeGroupWeekly for BFLGROUP + every SIM country, so the Monday
    06:00 GST run refreshes Volume Groups everywhere.

    RUN ON THE AZURE WMS DB.
    Prerequisite: migrate_wms_rpt_countryconfig_fix_pk.sql (PK must be
    (JobName, Country) or these inserts collide).

    What each row actually does — the two are NOT interchangeable:

      BFLGROUP        -> GenerateStoreDivGradesAsync writes dbo.StoreDivGrade,
                         covering every country in one pass. This is the row
                         OtsWeekly depends on: Generate OTS reads StoreDivGrade
                         and refuses to run unless it was written today.
      A named country -> writes dbo.LPM_StoreDivGrade_Country for that country
                         only, using its own bands from LPM_VolumeGroupRange_Country.

    So BFLGROUP is not "one of the countries" — dropping it breaks OTS even with
    all eight named countries active.

    The country list mirrors bfldata.dbo.DataSettings.SIMCountry (minus
    Ex2Locations), which is what the Container Allocation and OTS pages offer.
    Re-runnable: MERGE only inserts missing rows, so a deliberate switch-off of
    any single country is preserved.
*/

SET NOCOUNT ON;

DECLARE @seed TABLE (JobName nvarchar(100), Country nvarchar(50), IsActive bit);

INSERT INTO @seed (JobName, Country, IsActive)
VALUES ('VolumeGroupWeekly', 'BFLGROUP', 1),
       ('VolumeGroupWeekly', 'BAHRAIN',  1),
       ('VolumeGroupWeekly', 'ECOM',     1),
       ('VolumeGroupWeekly', 'KSA',      1),
       ('VolumeGroupWeekly', 'KUWAIT',   1),
       ('VolumeGroupWeekly', 'MALAYSIA', 1),
       ('VolumeGroupWeekly', 'OMAN',     1),
       ('VolumeGroupWeekly', 'QATAR',    1),
       ('VolumeGroupWeekly', 'UAE',      1);

MERGE dbo.WmsRptCountryConfig AS t
USING @seed AS s
  ON t.JobName = s.JobName AND t.Country = s.Country
WHEN NOT MATCHED BY TARGET THEN
  INSERT (JobName, Country, IsActive, UpdatedTS, UpdatedBy)
  VALUES (s.JobName, s.Country, s.IsActive,
          DATEADD(hour, 4, SYSUTCDATETIME()), 'migration');

/*  NOTE: every named country also needs band rows in
    LPM_VolumeGroupRange_Country, or its Monday run grades nothing. That table
    is ON-PREM (LPMSIM), not on this Azure DB, so it cannot be checked here.
    Run this separately against LPMSIM before trusting the first Monday:

        SELECT Country, COUNT(*) AS Bands
          FROM dbo.LPM_VolumeGroupRange_Country WITH (NOLOCK)
         GROUP BY Country ORDER BY Country;

    Any country active below but missing from that result will produce an empty
    grade set.
*/

-- Verify the toggles.
SELECT JobName, Country, IsActive, UpdatedTS, UpdatedBy
  FROM dbo.WmsRptCountryConfig
 WHERE JobName = 'VolumeGroupWeekly'
 ORDER BY Country;
