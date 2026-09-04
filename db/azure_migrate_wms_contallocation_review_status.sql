/*
    Azure WMS DB — dbo.WmsContAllocationReviewStatus
    -----------------------------------------------
    Buyer review state for an approved container, set by the user on the
    Container Allocation Data Sync page's "Approved Containers" grid.

    One row per ContNo. Status is free-form VARCHAR rather than a lookup table or
    CHECK constraint: the two values the page offers ('Good to GO', 'Buyer Review')
    are a workflow label, not referential data, and a CHECK would turn adding a
    third label into a migration. The page is the authority on what is offered.

    Lives on Azure WMS alongside WMS_ContAllocationDataSync_Log, not on LPMSIM's
    WMS_Cont_Allocation_Header: the header is shared with the LPMSIM application,
    and this is purely WMS-side review state.

    Run against the Azure WMS database.
*/
IF OBJECT_ID('dbo.WmsContAllocationReviewStatus', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsContAllocationReviewStatus
    (
        ContNo    VARCHAR(50)   NOT NULL,
        Status    VARCHAR(50)   NULL,   -- 'Good to GO' | 'Buyer Review' | NULL = not set
        Remarks   NVARCHAR(500) NULL,
        UpdatedBy VARCHAR(100)  NULL,
        UpdatedTS DATETIME2(0)  NOT NULL
            CONSTRAINT DF_WmsContAllocReview_UpdatedTS DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_WmsContAllocationReviewStatus PRIMARY KEY CLUSTERED (ContNo)
    );
END
GO
