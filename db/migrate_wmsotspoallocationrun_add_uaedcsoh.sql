/*
 * Adds dbo.WmsOtsPoAllocationRun.UaeDcSoh (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — Generate bulk-copies by column name, so
 * persisting fails outright against a table without this column.
 *
 * What it holds: the store's share of the UAE distribution centre pool
 * (racks..WHBoxItems, PalletCategory = 'Eligible', LPMDt inside the near
 * horizon), split per division by Week Sales across every country's stores
 * except ECOM.
 *
 * This value USED to be folded into Ex2DcSoh. It is now carried separately so
 * the grid can show the two side by side — a UAE figure silently included in
 * the Ex2 total invited double-counting when reading the row.
 *
 * Ex2DcSoh therefore now means ONLY the country's own export-warehouse stock
 * (LPM_Ex2ItemSOH.R1WHSOH). The OTS arithmetic is unchanged: it subtracts both
 * columns, which sum to what Ex2DcSoh alone used to carry.
 *
 * NOTE for anyone comparing runs across this change: rows written BEFORE it
 * have the combined figure in Ex2DcSoh and NULL here, so
 *   ISNULL(Ex2DcSoh,0) + ISNULL(UaeDcSoh,0)
 * is the comparable total on both sides.
 */

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'UaeDcSoh') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD UaeDcSoh INT NULL;
GO

PRINT 'dbo.WmsOtsPoAllocationRun.UaeDcSoh ready.';
