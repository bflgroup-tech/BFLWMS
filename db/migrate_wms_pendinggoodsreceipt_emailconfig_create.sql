/*
 * Creates dbo.WmsPendingGoodsReceiptEmailConfig on Azure WMS DB.
 *
 * Drives the PendingGoodsReceiptEmailScheduledService that hourly
 * (or on the configured cadence) mails the Pending Goods Receipt
 * report to the recipient list. Single-row config: always TOP 1.
 *
 * Seeds one inactive row so the admin page has something to edit
 * on first visit. Turn IsActive = 1 to arm.
 *
 * Idempotent.
 */
IF OBJECT_ID('dbo.WmsPendingGoodsReceiptEmailConfig', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsPendingGoodsReceiptEmailConfig (
        Id             INT           IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Recipients     NVARCHAR(2000) NOT NULL,      -- comma or semicolon separated To addresses
        IntervalHours  INT           NOT NULL,        -- 1 = every hour
        IsActive       BIT           NOT NULL
            CONSTRAINT DF_WPGREC_IsActive DEFAULT (0),
        LastRunTS      DATETIME2(0)  NULL,            -- last successful send (or attempt)
        LastRunStatus  NVARCHAR(500) NULL,            -- 'sent', 'skipped: no rows', 'error: ...'
        LastSentCount  INT           NULL,            -- container count in the last sent email
        UpdatedTS      DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WPGREC_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedBy      NVARCHAR(100) NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.WmsPendingGoodsReceiptEmailConfig)
BEGIN
    INSERT dbo.WmsPendingGoodsReceiptEmailConfig (Recipients, IntervalHours, IsActive, UpdatedBy)
    VALUES ('', 1, 0, 'system');
END;
