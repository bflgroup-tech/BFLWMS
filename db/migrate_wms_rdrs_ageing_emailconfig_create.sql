/*
 * Creates dbo.WmsRdRsAgeingEmailConfig on the Azure WMS DB.
 *
 * Drives RdRsAgeingEmailScheduledService — the weekly RD/RS ageing mail
 * (PalletType RD = RTV, RS = MC HOLD) built from racks.dbo.whboxitems.
 *
 * Single-row config, always TOP 1, same shape as
 * WmsPendingGoodsReceiptEmailConfig except the schedule: this one fires on a
 * DAY + TIME rather than an interval, because the business wants it every
 * Monday morning, not every N hours.
 *
 *   DayOfWeek  0 = Sunday .. 6 = Saturday, matching System.DayOfWeek so the
 *              poller compares without a lookup. Default 1 = Monday.
 *   HourGst /  Local wall-clock time in GST (UTC+4), the timezone every other
 *   MinuteGst  scheduled job in this app stamps its timestamps in.
 *
 * LastRunDate (a DATE, not a datetime) is what makes the fire-once-per-day
 * check cheap and honest: the poller runs every minute, so "have we already
 * sent today?" has to be a date comparison, not a window.
 *
 * Seeds one INACTIVE row so the admin page has something to edit on first
 * visit. Set IsActive = 1 from the page to arm it.
 *
 * Idempotent.
 */
IF OBJECT_ID('dbo.WmsRdRsAgeingEmailConfig', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsRdRsAgeingEmailConfig (
        Id             INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Recipients     NVARCHAR(2000) NOT NULL,       -- comma/semicolon separated To addresses
        CcRecipients   NVARCHAR(2000) NULL,           -- optional Cc list, same format
        DayOfWeek      TINYINT        NOT NULL        -- 0=Sun .. 6=Sat (System.DayOfWeek)
            CONSTRAINT DF_WRRAEC_DayOfWeek DEFAULT (1),
        HourGst        TINYINT        NOT NULL        -- 0..23, GST wall clock
            CONSTRAINT DF_WRRAEC_HourGst DEFAULT (8),
        MinuteGst      TINYINT        NOT NULL        -- 0..59
            CONSTRAINT DF_WRRAEC_MinuteGst DEFAULT (0),
        IsActive       BIT            NOT NULL
            CONSTRAINT DF_WRRAEC_IsActive DEFAULT (0),
        LastRunDate    DATE           NULL,           -- GST date of the last attempt (fire-once-per-day guard)
        LastRunTS      DATETIME2(0)   NULL,
        LastRunStatus  NVARCHAR(500)  NULL,           -- 'sent', 'skipped: no rows', 'error: ...'
        LastSentRows   INT            NULL,           -- table rows in the last sent mail
        LastSentQty    BIGINT         NULL,           -- total qty in the last sent mail
        UpdatedTS      DATETIME2(0)   NOT NULL
            CONSTRAINT DF_WRRAEC_UpdatedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        UpdatedBy      NVARCHAR(100)  NULL,
        CONSTRAINT CK_WRRAEC_DayOfWeek CHECK (DayOfWeek BETWEEN 0 AND 6),
        CONSTRAINT CK_WRRAEC_HourGst   CHECK (HourGst   BETWEEN 0 AND 23),
        CONSTRAINT CK_WRRAEC_MinuteGst CHECK (MinuteGst BETWEEN 0 AND 59)
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.WmsRdRsAgeingEmailConfig)
BEGIN
    -- Monday 08:00 GST, inactive until someone arms it from the admin page.
    INSERT dbo.WmsRdRsAgeingEmailConfig (Recipients, CcRecipients, DayOfWeek, HourGst, MinuteGst, IsActive, UpdatedBy)
    VALUES ('', '', 1, 8, 0, 0, 'system');
END;
