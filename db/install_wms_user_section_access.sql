/* =============================================================================
   Wms_UserSectionAccess — per-user grants for hideable page sub-sections
   (Wms.Core.SectionKeys). Same model as WmsUserCountryAccess: explicit grant
   or Admin role required; empty rows = section hidden.
   Run inside the Azure SQL WMS database.
   Idempotent.
   ============================================================================= */
IF OBJECT_ID('dbo.Wms_UserSectionAccess','U') IS NULL
CREATE TABLE dbo.Wms_UserSectionAccess (
    Username   NVARCHAR(100) NOT NULL,
    SectionKey NVARCHAR(50)  NOT NULL,
    GrantedTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsUserSectionAccess_TS DEFAULT(SYSDATETIME()),
    GrantedBy  NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_WmsUserSectionAccess PRIMARY KEY (Username, SectionKey)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsUserSectionAccess_User' AND object_id=OBJECT_ID('dbo.Wms_UserSectionAccess'))
    CREATE INDEX IX_WmsUserSectionAccess_User ON dbo.Wms_UserSectionAccess (Username);

-- Preserve current behavior: PRODSUMM_DTBW ("Daily Transfer Qty by Warehouse" on the
-- Production Summary Report) was previously hardcoded to this one user only.
IF NOT EXISTS (SELECT 1 FROM dbo.Wms_UserSectionAccess WHERE Username = 'shabeela.p@bflgroup.ae' AND SectionKey = 'PRODSUMM_DTBW')
    INSERT dbo.Wms_UserSectionAccess (Username, SectionKey, GrantedBy)
    VALUES ('shabeela.p@bflgroup.ae', 'PRODSUMM_DTBW', 'migration');

PRINT 'Wms_UserSectionAccess ready.';
