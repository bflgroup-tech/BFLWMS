/*
 * Adds dbo.WmsUserCountryAccess to the Azure WMS DB — per-user grant of
 * countries whose data the user may see in country-scoped pages/reports.
 *
 * Rules:
 *   - Users with role 'Admin' bypass this table (see all countries).
 *   - Non-admin users see ONLY the countries listed here.
 *   - Empty rows for a user  = no access anywhere.
 *
 * Grandfather policy for existing users: seed one row per (Username, SIMCountry)
 * for every distinct SIMCountry in WMS_DataSettings so nobody loses access on
 * deploy. Admins can then trim per user in the Users admin page.
 *
 * Idempotent.
 */

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WmsUserCountryAccess')
BEGIN
    CREATE TABLE dbo.WmsUserCountryAccess (
        Username  NVARCHAR(100) NOT NULL,
        Country   NVARCHAR(50)  NOT NULL,
        GrantedTS DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WmsUserCountryAccess_GrantedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        GrantedBy NVARCHAR(100) NOT NULL,
        CONSTRAINT PK_WmsUserCountryAccess PRIMARY KEY (Username, Country)
    );
END;

-- Grandfather: give every existing WmsUser a row for every known SIMCountry
-- (from WMS_DataSettings). Skips users who already have any explicit rows
-- so re-running never resets a trimmed access list.
INSERT dbo.WmsUserCountryAccess (Username, Country, GrantedBy)
SELECT u.Username, ds.SIMCountry, N'migration'
  FROM dbo.WmsUser u
 CROSS JOIN (
    SELECT DISTINCT SIMCountry
      FROM dbo.WMS_DataSettings
     WHERE SIMCountry IS NOT NULL
       AND LTRIM(RTRIM(SIMCountry)) <> ''
 ) ds
 WHERE NOT EXISTS (
    SELECT 1 FROM dbo.WmsUserCountryAccess a WHERE a.Username = u.Username);
