/*
 * Adds dbo.WmsOtsPoAllocationRun.DivisorWeeks (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — the Generate path bulk-copies by column name,
 * so persisting fails outright against a table without this column.
 *
 * What it holds: the number of fiscal weeks in the country's TARGET EOM month —
 * the month that lastWk (= current wk + NoOfLeadWeeks - 1) falls in, counted from
 * BFL_MFP_OUTBOUND_T1. That is the WeekAdjustment divisor:
 *
 *     WeekAdjustment = (TgtEOM - PrevMonthEOM) / DivisorWeeks
 *     CurrentEOW     = PrevMonthEOM + WeekAdjustment * NoOfLeadWeeks
 *
 * Previously the divisor was the PREV EOM month's week count and was not stored,
 * so the report could not be reconciled without re-deriving the fiscal calendar.
 * It varies per country, because lead weeks differ and therefore so does the
 * month lastWk lands in.
 *
 * Existing rows keep NULL — they were produced under the old divisor, and the
 * reader ISNULLs to 0, which the grid renders blank rather than as a wrong 4 or 5.
 */
IF COL_LENGTH('dbo.WmsOtsPoAllocationRun', 'DivisorWeeks') IS NULL
BEGIN
    ALTER TABLE dbo.WmsOtsPoAllocationRun ADD DivisorWeeks INT NULL;
END;
GO

-- Verify.
SELECT COL_LENGTH('dbo.WmsOtsPoAllocationRun','DivisorWeeks') AS DivisorWeeks_Len;
