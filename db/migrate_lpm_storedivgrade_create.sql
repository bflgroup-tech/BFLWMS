/*
 * Creates dbo.StoreDivGrade on LPMSIM (on-prem).
 *
 * Persists the (Store, Div, Month, Year) grade assignment produced by
 * OtsPoAllocationService.GenerateStoreDivGradesAsync — triggered by the
 * "Generate Volume Group" button on the OTS for PO Allocation page.
 *
 * Grade rule (per operator):
 *   ECOM stores (Country = 'ECOM')          -> 'Z' (fixed, AvgSalesPct null)
 *   Non-ECOM, top-K by AvgSalesPct per Div  -> 'A' where K = max(2, count(pct > 300))
 *   Rest                                    -> looked up in LPM_VolumeGroupRange
 *
 * Idempotent.
 */
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StoreDivGrade' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.StoreDivGrade (
        Month1       INT           NOT NULL,
        Year1        INT           NOT NULL,
        Country      NVARCHAR(20)  NOT NULL,
        StoreID      NVARCHAR(50)  NOT NULL,
        DivCode      INT           NOT NULL,
        SalesAmt     DECIMAL(18,2) NULL,
        AvgSalesAmt  DECIMAL(18,2) NULL,
        AvgSalesPct  DECIMAL(9,2)  NULL,
        Grade        NVARCHAR(5)   NULL,
        GeneratedTS  DATETIME2(0)  NOT NULL
            CONSTRAINT DF_StoreDivGrade_GeneratedTS DEFAULT (DATEADD(hour, 4, SYSUTCDATETIME())),
        GeneratedBy  NVARCHAR(100) NULL,
        CONSTRAINT PK_StoreDivGrade PRIMARY KEY (Month1, Year1, StoreID, DivCode)
    );
    CREATE INDEX IX_StoreDivGrade_MonthYear ON dbo.StoreDivGrade (Month1, Year1)
        INCLUDE (Country, DivCode, Grade, AvgSalesPct);
END;
