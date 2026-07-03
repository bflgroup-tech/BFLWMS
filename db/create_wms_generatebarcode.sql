/* =============================================================================
   Azure WMS DB — Item Encoding target table dbo.WMS_Generatebarcode.

   Columns per user spec plus:
     - Size varchar(50) NULL (dropdown filtered by Division)
     - CreateTS + CreatedBy audit columns (default GST)
     - Identity BarcodeId as surrogate PK

   Idempotent. Run on the Azure WMS DB.
   ============================================================================= */

IF OBJECT_ID('dbo.WMS_Generatebarcode', 'U') IS NULL
CREATE TABLE dbo.WMS_Generatebarcode (
    BarcodeId     BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WmsGenerateBarcode PRIMARY KEY CLUSTERED,
    Barcode       VARCHAR(30)   NOT NULL,
    Contno        VARCHAR(30)   NOT NULL,
    Groupcode     VARCHAR(20)   NULL,
    Catcode       VARCHAR(20)   NULL,
    Itemname      VARCHAR(200)  NULL,
    Trndate       SMALLDATETIME NULL,
    Tim1          VARCHAR(20)   NULL,
    Userid        INT           NULL,
    UPC           VARCHAR(30)   NULL,
    Remarks       VARCHAR(200)  NULL,
    BRAND         VARCHAR(50)   NULL,
    GENDER        VARCHAR(30)   NULL,
    Season        VARCHAR(10)   NULL,
    Style         VARCHAR(50)   NULL,
    OptionName    VARCHAR(100)  NULL,
    SubClass      VARCHAR(100)  NULL,
    Color         VARCHAR(50)   NULL,
    Size          VARCHAR(50)   NULL,
    Photosize     VARCHAR(30)   NULL,
    Currency      VARCHAR(10)   NULL,
    RRp           NUMERIC(18,2) NULL,
    Division      VARCHAR(100)  NULL,
    [Class]       VARCHAR(100)  NULL,
    UpdatedtoStg  VARCHAR(1)    NULL,
    UpdatedDate   SMALLDATETIME NULL,
    WMSclass      VARCHAR(100)  NULL,
    WMSSubclass   VARCHAR(100)  NULL,
    Department    VARCHAR(100)  NULL,
    Family        VARCHAR(100)  NULL,
    CostPrice     DECIMAL(18,2) NULL,
    COO           VARCHAR(50)   NULL,
    CreateTS      DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WGB_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy     NVARCHAR(100) NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WGB_Contno'
                 AND object_id = OBJECT_ID('dbo.WMS_Generatebarcode'))
    CREATE INDEX IX_WGB_Contno
        ON dbo.WMS_Generatebarcode (Contno, CreateTS DESC)
        INCLUDE (Barcode, Itemname, BRAND);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WGB_Barcode'
                 AND object_id = OBJECT_ID('dbo.WMS_Generatebarcode'))
    CREATE INDEX IX_WGB_Barcode
        ON dbo.WMS_Generatebarcode (Barcode);
GO

PRINT 'Azure WMS: dbo.WMS_Generatebarcode ready.';
