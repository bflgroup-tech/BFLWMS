/*
 * Adds LeadIntransit + LeadDCSOH columns to dbo.WmsOtsPoAllocationRun on
 * the Azure WMS DB.
 *
 * LeadIntransit = per-country vTransferDetail qty whose LPMDt is on/before
 *                 the 1st of the month that (today + LeadWeeks * 7) lands in,
 *                 restricted to trfno in racks..InTransit_ExportShipment where
 *                 country=<country> and intransit='Y', then split across the
 *                 stores in each (Country, DivCode) group by largest-remainder.
 *                 UAE = 0 (same policy as InTransit).
 *
 * LeadDCSOH     = per-country {DataName}..whboxitemexport qty whose LPMDt is
 *                 on/before the same cutoff, split across the stores in each
 *                 (Country, DivCode) group by largest-remainder.
 *
 * The existing InTransit + Ex2DcSoh columns are unchanged; the new columns
 * are additive, displayed BEFORE InTransit on the OTS PO Allocation grid.
 *
 * Idempotent.
 */

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'LeadIntransit') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD LeadIntransit INT NULL;

IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'LeadDCSOH') IS NULL
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD LeadDCSOH INT NULL;
