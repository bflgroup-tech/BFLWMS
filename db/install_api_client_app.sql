/* =============================================================================
   ApiClientApp — registered machine-to-machine API clients (OAuth2
   client-credentials grant) for the WMS API under /api/v1.
   Run inside the Azure SQL WMS database.
   Idempotent.
   ============================================================================= */
IF OBJECT_ID('dbo.ApiClientApp','U') IS NULL
CREATE TABLE dbo.ApiClientApp (
    ClientId   NVARCHAR(64)  NOT NULL,
    Name       NVARCHAR(200) NOT NULL,
    SecretHash NVARCHAR(200) NOT NULL,
    Enabled    BIT           NOT NULL CONSTRAINT DF_ApiClientApp_Enabled DEFAULT(1),
    CreateTS   DATETIME2(0)  NOT NULL CONSTRAINT DF_ApiClientApp_TS DEFAULT(SYSDATETIME()),
    CreatedBy  NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_ApiClientApp PRIMARY KEY (ClientId)
);

PRINT 'ApiClientApp ready.';
