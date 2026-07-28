/*
 * Renames dbo.WMS_ContAllocationData.Pass4RatioCap -> RatioSkuMax
 * on LPMSIM (idempotent).
 *
 * Rationale — the column stores the SKU-max value the store contributed
 * to the Pass 4 ratio denominator (FSMRR: OTS tier picker cap;
 * FMMPO: raw MinMax). "Pass4RatioCap" was too vague — operators reading
 * the row couldn't tell that this was the SKU-max used in the ratio
 * calculation. RatioSkuMax spells it out.
 *
 * Also: FMMPO Pass 4 was not populating this column at all (only FSMRR
 * was). The code change alongside this migration fixes that so both
 * algorithms stamp it.
 */
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass4RatioCap') IS NOT NULL
   AND COL_LENGTH('dbo.WMS_ContAllocationData', 'RatioSkuMax') IS NULL
BEGIN
    EXEC sp_rename N'dbo.WMS_ContAllocationData.Pass4RatioCap', N'RatioSkuMax', N'COLUMN';
END;
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'RatioSkuMax') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationData ADD RatioSkuMax INT NULL;
END;
