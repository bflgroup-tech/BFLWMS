/*
    LPMSIM.dbo.WMS_ContAllocationData — add Pass5Qty
    -----------------------------------------------
    Pass 5 is the planner's manual top-up of Pass-4 flagged quantity: qty that the
    automatic passes could not place is allocated by hand to chosen stores, up to a
    chosen SKU Max tier, then round-robin beyond it if any remains.

    Pass5Qty carries the same meaning as Pass1Qty..Pass4Qty — how much of this row's
    AllocatedQty came from that pass — so a row topped up by the planner shows both
    its automatic share and the manual one rather than silently growing.

    Only LPMSIM needs it. The Azure / WMS-Prod sync copies AllocatedQty (one row per
    piece) and never reads the per-pass columns, and Pass 5 is refused once a
    container has been synced anyway.

    Run against LPMSIM. Safe to re-run.
*/
IF COL_LENGTH('dbo.WMS_ContAllocationData', 'Pass5Qty') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_ContAllocationData ADD Pass5Qty INT NULL;
END
GO
