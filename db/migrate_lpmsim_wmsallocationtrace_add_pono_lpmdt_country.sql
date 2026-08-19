/*
 * Adds dbo.WmsAllocationTrace.PONo, .LPMDt and .Country (idempotent).
 *
 * RUN ON LPMSIM BEFORE DEPLOYING — SqlBulkCopy maps every DataTable column by
 * name, so the trace flush fails outright on a table without these.
 *
 * Why: allocation walks (OraPONo, Division, LPMDt) combos, one PO line item at
 * a time, so a trace row is only fully interpretable once you know which combo
 * produced it. Previously that meant joining back to usa.dbo.usaorgfile_LPM on
 * (ContNo, Itemcode) — and that join is ambiguous when the same item appears on
 * more than one PO in a container. Country comes from the store's OTS row and
 * saves a second join to answer "which countries did this pass touch".
 *
 * NULL on the synthetic Pass 4 'Flagged' row for Country — it has no store.
 * PONo / LPMDt are populated on every row including the synthetic ones.
 */
IF COL_LENGTH('dbo.WmsAllocationTrace', 'PONo') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD PONo VARCHAR(50) NULL;
END;
GO

IF COL_LENGTH('dbo.WmsAllocationTrace', 'LPMDt') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD LPMDt DATE NULL;
END;
GO

IF COL_LENGTH('dbo.WmsAllocationTrace', 'Country') IS NULL
BEGIN
    ALTER TABLE dbo.WmsAllocationTrace ADD Country VARCHAR(50) NULL;
END;
GO

-- Verify.
SELECT COL_LENGTH('dbo.WmsAllocationTrace','PONo')    AS PONo_Len,
       COL_LENGTH('dbo.WmsAllocationTrace','LPMDt')   AS LPMDt_Len,
       COL_LENGTH('dbo.WmsAllocationTrace','Country') AS Country_Len;
