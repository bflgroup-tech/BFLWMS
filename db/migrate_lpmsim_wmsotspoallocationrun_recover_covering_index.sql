/*
    LPMSIM.dbo.WmsOtsPoAllocationRun — restore the covering index
    -------------------------------------------------------------
    Run against LPMSIM. Safe to re-run.

    WHY
    ---
    IX_WmsOtsPoAllocationRun_MonthYearOTSDate was created as a COVERING index for
    the Load query: key ([Year], [Month], OTSDate, Country, DivCode) plus an
    INCLUDE list holding every other column that query returned at the time.

    Thirteen columns have been added to the table since, and Load reads all of
    them:

        TgtEOMMonth      PrevEOMMonth     DivisorWeeks
        NoOfLeadWeeks    PrevMonthEOM     WeekAdjustment
        LeadIntransit    CurrentEOW       CurrentWeek
        LeadDCSOH                         TargetWeek
        UaeDcSoh                          WeeksMultiplier

    None of them are in the INCLUDE, so the index stopped covering. SQL Server
    then either does a key lookup per row or abandons the index and scans the
    table — and this table keeps EVERY daily run, so that scan grows by roughly
    2,400 rows a day. That is the "Load is slow, and getting slower" symptom.

    WHAT THIS DOES
    --------------
    Recreates the index with the full INCLUDE list. Key columns are unchanged, so
    nothing else that uses this index changes behaviour.

    DROP_EXISTING = ON rebuilds in one operation rather than leaving the table
    without the index in between — on a table this size a DROP then CREATE would
    leave every reader scanning for the duration.

    The index is wide because the query is wide. That is the trade: this table is
    written once a day in bulk and read many times a day, so paying at write time
    for a covering read is the right way round.

    AFTER RUNNING
    -------------
    If Load is still slow, the next thing to look at is table size rather than
    indexing — see the row count below. Nothing prunes old OTSDate runs today.
*/

SET NOCOUNT ON;

IF OBJECT_ID('dbo.WmsOtsPoAllocationRun', 'U') IS NULL
BEGIN
    RAISERROR('dbo.WmsOtsPoAllocationRun does not exist in this database. Run this against LPMSIM.', 16, 1);
    RETURN;
END;

-- How big has it grown, and over how many runs? Printed before and after so the
-- effect is visible in the Messages pane.
DECLARE @rows BIGINT, @dates INT, @oldest DATE, @newest DATE;
SELECT @rows   = COUNT_BIG(*),
       @dates  = COUNT(DISTINCT OTSDate),
       @oldest = MIN(OTSDate),
       @newest = MAX(OTSDate)
  FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK);

PRINT CONCAT('Rows: ', @rows, '   Distinct OTSDate: ', @dates,
             '   Oldest: ', CONVERT(varchar(10), @oldest, 120),
             '   Newest: ', CONVERT(varchar(10), @newest, 120));

-- What is actually on the table today? Printed so a surprise (no index at all,
-- or a differently-named one) is visible rather than inferred.
PRINT '--- existing indexes ---';
DECLARE @ix NVARCHAR(MAX) = N'';
SELECT @ix = @ix + '  ' + i.name + ' (' + i.type_desc + ')' + CHAR(13) + CHAR(10)
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID('dbo.WmsOtsPoAllocationRun')
   AND i.name IS NOT NULL;
PRINT ISNULL(NULLIF(@ix, N''), N'  (none)');

-- DROP_EXISTING only when it exists — on this database the index was missing
-- entirely, which made an unconditional DROP_EXISTING = ON fail outright
-- (Msg 7999). Create and rebuild are handled as the two separate cases they are.
IF EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = N'IX_WmsOtsPoAllocationRun_MonthYearOTSDate'
              AND object_id = OBJECT_ID(N'dbo.WmsOtsPoAllocationRun'))
BEGIN
    PRINT 'Index present - rebuilding with the full INCLUDE list...';
    CREATE INDEX IX_WmsOtsPoAllocationRun_MonthYearOTSDate
        ON dbo.WmsOtsPoAllocationRun ([Year], [Month], OTSDate, Country, DivCode)
        INCLUDE (
            StoreID, StoreName, Division, VolumeGroup, PriorityRank,
            TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh,
            CountingWIP, OtsQtyToday, OtsPercentToday,
            TgtEOMMonth, NoOfLeadWeeks, LeadIntransit, LeadDCSOH, UaeDcSoh,
            PrevEOMMonth, PrevMonthEOM, DivisorWeeks, WeekAdjustment,
            CurrentWeek, TargetWeek, WeeksMultiplier, CurrentEOW
        )
        WITH (DROP_EXISTING = ON, ONLINE = OFF, FILLFACTOR = 90);
END
ELSE
BEGIN
    PRINT 'Index MISSING - creating it. Every Load has been scanning the whole table.';
    CREATE INDEX IX_WmsOtsPoAllocationRun_MonthYearOTSDate
        ON dbo.WmsOtsPoAllocationRun ([Year], [Month], OTSDate, Country, DivCode)
        INCLUDE (
            StoreID, StoreName, Division, VolumeGroup, PriorityRank,
            TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh,
            CountingWIP, OtsQtyToday, OtsPercentToday,
            TgtEOMMonth, NoOfLeadWeeks, LeadIntransit, LeadDCSOH, UaeDcSoh,
            PrevEOMMonth, PrevMonthEOM, DivisorWeeks, WeekAdjustment,
            CurrentWeek, TargetWeek, WeeksMultiplier, CurrentEOW
        )
        WITH (FILLFACTOR = 90);
END;

PRINT 'IX_WmsOtsPoAllocationRun_MonthYearOTSDate is now a covering index for the Load query.';

/*
    OPTIONAL — retention.
    --------------------
    Nothing deletes old runs. If the row count above is large and Load is still
    slow after this rebuild, keeping (say) the last 120 days is the next step.
    Left commented out deliberately: deleting run history is a business decision,
    not a performance one, and the OTS page's Run Date picker reads these rows.

    DELETE FROM dbo.WmsOtsPoAllocationRun
     WHERE OTSDate < DATEADD(day, -120, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS date));
*/
