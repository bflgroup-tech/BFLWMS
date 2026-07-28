/*
 * Adds dbo.WmsAllocationTrace.RatioSkuMax (idempotent).
 *
 * Populated on Pass 4 rows only (both FSMRR and FMMPO) with the store's
 * SkuMax contribution to the ratio numerator:
 *   FSMRR Pass 4: OTS tier picker cap  (CapFor(r))
 *   FMMPO Pass 4: raw MinMax value     (RawMinMaxFor(r))
 * NULL for Pass 1b / 2 / 3 rows — those passes don't use the ratio.
 *
 * Mirrors the RatioSkuMax column on WMS_ContAllocationData so trace and
 * allocation rows can be joined and compared.
 */
IF COL_LENGTH('dbo.WmsAllocationTrace', 'RatioSkuMax') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD RatioSkuMax INT NULL;
END;
