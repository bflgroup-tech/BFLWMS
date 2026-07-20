/*
 * Adds MonthlyWeightage to dbo.LPM_Weekly_SalesAmt (LPMSIM).
 *
 * Populator should mirror dbo.LPM_MonthlyWeight.Weightage (indexed by
 * week-of-month) onto every row for recon clarity — makes it possible
 * to hand-verify a MonthlySalesAmt calculation without re-joining.
 *
 * DECIMAL(9,4); nullable so historic rows without a weightage stay valid
 * until backfilled.
 *
 * Idempotent.
 */
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.LPM_Weekly_SalesAmt')
      AND name = 'MonthlyWeightage'
)
BEGIN
    ALTER TABLE dbo.LPM_Weekly_SalesAmt
        ADD MonthlyWeightage DECIMAL(9,4) NULL;
END;
