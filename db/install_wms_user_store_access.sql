/* =============================================================================
   Wms_UserStoreAccess — per-user grants restricting the STORE dropdown on
   Transfer/GIN/GRN History to specific stores. Unlike WmsUserCountryAccess /
   Wms_UserSectionAccess, this is OPT-IN, not restrict-by-default: a user with
   no rows here sees every store their country access already allows
   (unchanged behavior) — only a user with at least one row is narrowed down
   to just those stores. Admin role bypasses this table.
   Run inside the Azure SQL WMS database.
   Idempotent.
   ============================================================================= */
IF OBJECT_ID('dbo.Wms_UserStoreAccess','U') IS NULL
CREATE TABLE dbo.Wms_UserStoreAccess (
    Username   NVARCHAR(100) NOT NULL,
    StoreName  NVARCHAR(100) NOT NULL,
    GrantedTS  DATETIME2(0)  NOT NULL CONSTRAINT DF_WmsUserStoreAccess_TS DEFAULT(SYSDATETIME()),
    GrantedBy  NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_WmsUserStoreAccess PRIMARY KEY (Username, StoreName)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_WmsUserStoreAccess_User' AND object_id=OBJECT_ID('dbo.Wms_UserStoreAccess'))
    CREATE INDEX IX_WmsUserStoreAccess_User ON dbo.Wms_UserStoreAccess (Username);

-- Initial grants.
INSERT dbo.Wms_UserStoreAccess (Username, StoreName, GrantedBy)
SELECT v.Username, v.StoreName, 'migration'
FROM (VALUES
    ('ali.i@bflgroup.ae',     'Wmall'),
    ('ali.i@bflgroup.ae',     'AbuMall'),
    ('charie.epil@bflgroup.ae','LFLMOTORCITY'),
    ('charie.epil@bflgroup.ae','BFLFLAGSHIP'),
    ('gjoy@bflgroup.ae',      'BFLDCC'),
    ('gjoy@bflgroup.ae',      'BFLGHURAIR')
) AS v(Username, StoreName)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Wms_UserStoreAccess x
     WHERE x.Username = v.Username AND x.StoreName = v.StoreName
);

PRINT 'Wms_UserStoreAccess ready.';
