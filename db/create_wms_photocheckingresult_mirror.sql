/*
 * dbo.WMS_PhotoCheckingResult_Mirror
 *
 * Azure WMS mirror of on-prem online.dbo.PhotoCheckingResult (WMSPRODDB).
 * Populated by the "WMSPRODDB -> Azure WMS" reverse-pull destination on the
 * Container Allocation Data Sync page. One insert per source row, per ContNo
 * sync. Column set mirrors the forward-push writer in
 * ContainerAllocationDataSyncService.BuildPhotoCheckingResultDataTable.
 *
 * Idempotent: safe to re-run.
 */
IF OBJECT_ID(N'dbo.WMS_PhotoCheckingResult_Mirror', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WMS_PhotoCheckingResult_Mirror
    (
        MirrorId          INT             IDENTITY(1,1) NOT NULL,
        ContNo            NVARCHAR(50)    NULL,
        TrnDate           DATETIME        NULL,
        Time1             TIME(0)         NULL,
        UPC               NVARCHAR(50)    NULL,
        Itemcode          NVARCHAR(50)    NULL,
        GroupCode         NVARCHAR(50)    NULL,
        Season            NVARCHAR(50)    NULL,
        Department        NVARCHAR(50)    NULL,
        Division          NVARCHAR(50)    NULL,
        FinalResult       NVARCHAR(50)    NULL,
        ResultType        NVARCHAR(50)    NULL,
        Qty               INT             NULL,
        QtyIssue          INT             NULL,
        Itemname          NVARCHAR(255)   NULL,
        Barcode           NVARCHAR(50)    NULL,
        SalesPrice        NVARCHAR(30)    NULL,
        TcmContno         NVARCHAR(50)    NULL,
        BuildingCategory  NVARCHAR(50)    NULL,
        LPMDt             DATETIME        NULL,
        LPMBoxNO          NVARCHAR(50)    NULL,
        ORAPONo           NVARCHAR(50)    NULL,
        Style             NVARCHAR(50)    NULL,
        Remarks           NVARCHAR(255)   NULL,
        StoreId           NVARCHAR(50)    NULL,
        SyncedBy          NVARCHAR(128)   NULL,
        SyncedTS          DATETIME2(0)    NOT NULL CONSTRAINT DF_WMS_PhotoCheckingResult_Mirror_SyncedTS DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_WMS_PhotoCheckingResult_Mirror PRIMARY KEY CLUSTERED (MirrorId)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WMS_PhotoCheckingResult_Mirror_ContNo' AND object_id = OBJECT_ID(N'dbo.WMS_PhotoCheckingResult_Mirror'))
BEGIN
    CREATE INDEX IX_WMS_PhotoCheckingResult_Mirror_ContNo
        ON dbo.WMS_PhotoCheckingResult_Mirror (ContNo);
END;
