/*
    LPMSIM.dbo.WMS_Cont_Allocation_Header — add Store_Allocation (Y/N)
    -----------------------------------------------------------------
    Marks which kind of allocation produced a header row. Every run written by
    the Container Allocation page is PO Allocation, so it stamps 'N'; a store
    allocation writer sets 'Y'.

    Defined as NOT NULL with a 'N' default and a Y/N check constraint, so the
    flag is always one of two known values — a nullable column would have grown a
    third, unlabelled state ("written before the flag existed") that every reader
    would then have to special-case.

    Rows that predate this change are backfilled to 'N': the only writer up to
    now has been PO Allocation, so that is what they factually are.

    Run against LPMSIM. Safe to re-run.
*/
IF COL_LENGTH('dbo.WMS_Cont_Allocation_Header', 'Store_Allocation') IS NULL
BEGIN
    ALTER TABLE dbo.WMS_Cont_Allocation_Header ADD Store_Allocation CHAR(1) NULL;
END
GO

-- Every existing header row was produced by PO Allocation.
UPDATE dbo.WMS_Cont_Allocation_Header
   SET Store_Allocation = 'N'
 WHERE Store_Allocation IS NULL;
GO

-- Default BEFORE the NOT NULL switch, so any insert racing this script still
-- has a value to land on.
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE name = 'DF_CAH_StoreAllocation'
                  AND parent_object_id = OBJECT_ID('dbo.WMS_Cont_Allocation_Header'))
BEGIN
    ALTER TABLE dbo.WMS_Cont_Allocation_Header
        ADD CONSTRAINT DF_CAH_StoreAllocation DEFAULT('N') FOR Store_Allocation;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID('dbo.WMS_Cont_Allocation_Header')
                  AND name = 'Store_Allocation' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.WMS_Cont_Allocation_Header
        ALTER COLUMN Store_Allocation CHAR(1) NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                WHERE name = 'CK_CAH_StoreAllocation'
                  AND parent_object_id = OBJECT_ID('dbo.WMS_Cont_Allocation_Header'))
BEGIN
    ALTER TABLE dbo.WMS_Cont_Allocation_Header
        ADD CONSTRAINT CK_CAH_StoreAllocation
            CHECK (Store_Allocation IN ('Y', 'N'));
END
GO

PRINT 'WMS_Cont_Allocation_Header.Store_Allocation ready (default N).';
