/*
 * Adds dbo.WmsAllocationTrace.POLineSizeQty (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — the trace flush bulk-copies by column name,
 * so it fails outright against a table without this column. Note that failure
 * only bites when Trace Allocation is ticked.
 *
 * What it holds: the PO line quantity for the item the trace row is about —
 * usaorgfile_LPM.orgqty for that (ContNo, OraPONo, Division, LPMDt, ItemCode)
 * combination. Same number RemainingBefore starts at on the first pass.
 *
 * Why: every other quantity on a trace row is relative (Cap, Take,
 * RemainingBefore/After), so reading "this store took 9" meant nothing without
 * knowing whether the line held 100 pieces or 5,000. It also makes the SKU Max
 * band choice checkable in place — the band is selected by PoQty falling inside
 * PoQtyFrom..PoQtyTo, and that input was not on the row.
 *
 * Rows written before this are NULL here, not 0 — an unknown line size, not a
 * line of nothing.
 */

IF COL_LENGTH('dbo.WmsAllocationTrace', 'POLineSizeQty') IS NULL
    ALTER TABLE dbo.WmsAllocationTrace ADD POLineSizeQty INT NULL;
GO

PRINT 'dbo.WmsAllocationTrace.POLineSizeQty ready.';
