/*
 * LPMSIM migration — Bypass Pass 1b audit + MinMinCoverPct column.
 *
 * New Bypass Pass 1b logic (v1.0.348+):
 *   - When operator ticks "Bypass Pass 1b" on Container Allocation, the
 *     per-item PoQty threshold is no longer used to decide Pass 1b
 *     eligibility. Instead the service computes MinMinCoverPct per item:
 *
 *       ABCReqdStock  = sum of max(0, tier - SOH) across A/B/C stores
 *                       with LiveOts >= 0 (tier picked by the Pass-2
 *                       tier picker: MinMax / IdealMax / MaxMax).
 *       MinMinCoverPct = ABCReqdStock / PoQty * 100.
 *
 *   - MinMinCoverPct >= 100  ->  skip Pass 1b entirely (start with Pass 2).
 *   - MinMinCoverPct <  100  ->  stage 1: allocate (tier - SOH) to A/B/C
 *                                stores; stage 2: MinMin=1 to remaining
 *                                A-H OTS>=0 stores.
 *
 *   - Every item's calc is audited in dbo.Pass1ByPass, and MinMinCoverPct
 *     lands on every WMS_ContAllocationData row for the item.
 *
 * Idempotent. Safe to re-run. No data backfill.
 */

-- ---- 1. New audit table ------------------------------------------------
IF OBJECT_ID(N'dbo.Pass1ByPass', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Pass1ByPass
    (
        Id             BIGINT       IDENTITY(1,1) NOT NULL,
        ContNo         VARCHAR(50)  NOT NULL,
        PONo           VARCHAR(50)  NOT NULL,
        Itemcode       VARCHAR(50)  NOT NULL,
        POQty          INT          NOT NULL,
        ABCMax         INT          NOT NULL,
        ABCSOH         INT          NOT NULL,
        ABCReqdStock   INT          NOT NULL,
        MinMinCoverPct DECIMAL(9,2) NOT NULL,
        CreatedTS      DATETIME2    NOT NULL CONSTRAINT DF_Pass1ByPass_CreatedTS DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Pass1ByPass PRIMARY KEY (Id)
    );
    CREATE INDEX IX_Pass1ByPass_ContNo ON dbo.Pass1ByPass(ContNo);
END;

-- ---- 2. New column on WMS_ContAllocationData ---------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE object_id = OBJECT_ID(N'dbo.WMS_ContAllocationData')
       AND name = 'MinMinCoverPct'
)
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationData
      ADD MinMinCoverPct DECIMAL(9,2) NULL;
END;
