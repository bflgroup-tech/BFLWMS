/*
 * Creates dbo.WmsPlanningFlag on LPMSIM.
 *
 * Populated by FillMinMinPlusOthers Pass 4 when a container item's
 * remaining Qty (>=10% of its PO qty) can't be safely distributed to
 * top-grade stores. The Azure copy is being retired — inserts now
 * target this LPMSIM table so the app stops writing directly to Azure.
 *
 * Idempotent.
 */
IF OBJECT_ID('dbo.WmsPlanningFlag', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsPlanningFlag (
        FlagId       BIGINT        IDENTITY(1,1) NOT NULL,
        FlaggedTS    DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WPF_FlaggedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        ContNo       NVARCHAR(50)  NOT NULL,
        PONo         NVARCHAR(50)  NULL,
        ItemCode     NVARCHAR(50)  NOT NULL,
        DivCode      INT           NULL,
        PoQty        INT           NOT NULL,
        RemainingQty INT           NOT NULL,
        RunOption    NVARCHAR(60)  NOT NULL,
        FlaggedBy    NVARCHAR(100) NULL,
        CONSTRAINT PK_WmsPlanningFlag PRIMARY KEY (FlagId)
    );
    CREATE INDEX IX_WmsPlanningFlag_ContNo ON dbo.WmsPlanningFlag (ContNo, ItemCode);
    CREATE INDEX IX_WmsPlanningFlag_FlaggedTS ON dbo.WmsPlanningFlag (FlaggedTS DESC);
END;
