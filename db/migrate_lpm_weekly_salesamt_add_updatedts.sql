/*
 * Adds UpdatedTS to dbo.LPM_Weekly_SalesAmt (LPMSIM).
 *
 * Stamped by GenerateStoreDivGradesAsync every time "Generate Volume
 * Group" runs — a per-row audit trail showing when its MonthlyWeightage
 * was last refreshed from dbo.LPM_MonthlyWeight for the picked RunMonth.
 *
 * Nullable so historic rows without a refresh stay valid until the next
 * Generate run touches them.
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.LPM_Weekly_SalesAmt')
      AND name = 'UpdatedTS'
)
BEGIN
    ALTER TABLE dbo.LPM_Weekly_SalesAmt
        ADD UpdatedTS DATETIME2(0) NULL;
END;
