/* =============================================================================
   ApiRequestLog — one row per request made to /api/v1/*. Deliberately holds no
   request/response body content — method, path, status code, client identity,
   and timing only.
   Run inside the Azure SQL WMS database.
   Idempotent.
   ============================================================================= */
IF OBJECT_ID('dbo.ApiRequestLog','U') IS NULL
CREATE TABLE dbo.ApiRequestLog (
    Id         BIGINT IDENTITY(1,1) NOT NULL,
    Timestamp  DATETIME2(0)  NOT NULL,
    Method     NVARCHAR(10)  NOT NULL,
    Path       NVARCHAR(300) NOT NULL,
    StatusCode INT           NOT NULL,
    ClientId   NVARCHAR(64)  NULL,
    ClientName NVARCHAR(200) NULL,
    ClientIp   NVARCHAR(45)  NULL,
    DurationMs INT           NOT NULL,
    CONSTRAINT PK_ApiRequestLog PRIMARY KEY (Id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ApiRequestLog_Timestamp'
                 AND object_id = OBJECT_ID('dbo.ApiRequestLog'))
    CREATE INDEX IX_ApiRequestLog_Timestamp ON dbo.ApiRequestLog (Timestamp DESC);

PRINT 'ApiRequestLog ready.';
