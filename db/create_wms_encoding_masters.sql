/* =============================================================================
   Azure WMS DB — Item Encoding master tables:

     dbo.WMSGender      (Gender + audit)
     dbo.WMSColor       (Color  + audit)
     dbo.WMSSizeMaster  (mirrors usa.dbo.MFCSizeMaster shape: Size, DivID,
                         CreateTS, Remarks — plus surrogate PK and CreatedBy)

   Idempotent. Run on the Azure WMS DB. Data for WMSSizeMaster is populated
   from usa..MFCSizeMaster via an app-side sync (Phase 2), or via a manual
   one-shot INSERT ... SELECT from a linked server if you have one.
   ============================================================================= */

/* ---- Gender ---- */
IF OBJECT_ID('dbo.WMSGender', 'U') IS NULL
CREATE TABLE dbo.WMSGender (
    GenderId  INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WMSGender PRIMARY KEY CLUSTERED,
    Gender    VARCHAR(30)   NOT NULL,
    CreateTS  DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WMSGender_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy NVARCHAR(100) NULL,
    CONSTRAINT UQ_WMSGender_Gender UNIQUE (Gender)
);
GO

/* ---- Color ---- */
IF OBJECT_ID('dbo.WMSColor', 'U') IS NULL
CREATE TABLE dbo.WMSColor (
    ColorId   INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WMSColor PRIMARY KEY CLUSTERED,
    Color     VARCHAR(50)   NOT NULL,
    CreateTS  DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WMSColor_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy NVARCHAR(100) NULL,
    CONSTRAINT UQ_WMSColor_Color UNIQUE (Color)
);
GO

/* ---- Size (per Division) ---- */
IF OBJECT_ID('dbo.WMSSizeMaster', 'U') IS NULL
CREATE TABLE dbo.WMSSizeMaster (
    SizeId    INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_WMSSizeMaster PRIMARY KEY CLUSTERED,
    Size      VARCHAR(50)   NOT NULL,
    DivID     INT           NOT NULL,
    Remarks   VARCHAR(200)  NULL,
    CreateTS  DATETIME2(0)  NOT NULL
        CONSTRAINT DF_WMSSizeMaster_CreateTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
    CreatedBy NVARCHAR(100) NULL,
    CONSTRAINT UQ_WMSSizeMaster_Div_Size UNIQUE (DivID, Size)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_WMSSizeMaster_DivID'
                 AND object_id = OBJECT_ID('dbo.WMSSizeMaster'))
    CREATE INDEX IX_WMSSizeMaster_DivID
        ON dbo.WMSSizeMaster (DivID)
        INCLUDE (Size);
GO

PRINT 'Azure WMS: dbo.WMSGender, dbo.WMSColor, dbo.WMSSizeMaster ready.';
