/* =============================================================================
   Azure WMS DB — dbo.WMSBrandMaster.

   Master brand list for the Item Encoding page (searchable fallback when
   the picked container's manifest doesn't include the brand the operator
   needs). Seed data by copying from usa.dbo.BrandMaster manually
   (INSERT ... VALUES) after this DDL runs.

   Idempotent. Run on the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WMSBrandMaster', 'U') IS NULL
CREATE TABLE dbo.WMSBrandMaster (
    BrandId    INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WMSBrandMaster PRIMARY KEY CLUSTERED,
    BrandName  VARCHAR(100)  NOT NULL,
    CreateTS   DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WMSBrandMaster_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy  NVARCHAR(100) NULL,
    CONSTRAINT UQ_WMSBrandMaster_BrandName UNIQUE (BrandName)
);
GO

PRINT 'Azure WMS: dbo.WMSBrandMaster ready.';
