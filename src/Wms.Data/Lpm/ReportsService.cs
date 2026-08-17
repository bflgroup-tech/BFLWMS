using System.Data;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Reports service — Missing/Excess from Production and related cross-DB reports.
///
/// Country list comes from the UAE master backup connection
/// (bfldata.dbo.datasettings.Simcountry). Per-report queries use the
/// per-country connection (IOnPremConnectionResolver.GetCountryConnectionString).
/// </summary>
public class ReportsService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 60;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    // All report queries currently hit OnPremBackup (UAE master) via 3-part naming —
    // same as ContainerAllocationService — since per-country connection strings
    // aren't configured. Wire to GetCountryConnectionString later if needed.
    private SqlConnection OpenCountry(string country) => OpenOnPremBackup();

    // ===================== Snapshot-backed reads (Azure WMS DB) =====================
    // These are the methods the Missing/Excess page calls on Load. They read
    // pre-computed snapshot tables populated by MissingExcessSnapshotService.

    public async Task<List<BoxSummaryMonthRow>> BoxSummaryByMonthFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxSummaryMonthRow>(new CommandDefinition(@"
            SELECT CONVERT(varchar(7), ClosedDt, 120)  AS [Month],
                   COUNT(DISTINCT BoxNo)                AS BoxCount,
                   SUM(MissQty)                         AS MissQty,
                   SUM(ExcessQty)                       AS ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             GROUP BY CONVERT(varchar(7), ClosedDt, 120)
             ORDER BY [Month]",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<BoxSummaryRow>> BoxSummaryFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxSummaryRow>(new CommandDefinition(@"
            SELECT BoxNo, ClosedDt, ClosedBy, MissQty, ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY ClosedBy DESC, ClosedDt DESC",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<BoxDetailCombinedRow>> BoxDetailCombinedFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxDetailCombinedRow>(new CommandDefinition(@"
            SELECT BoxNo, PreparedBy, ItemCode, Qty, QtyIssued, MissingQty, ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxDetail
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY BoxNo, ItemCode",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ItemSummaryByDivDeptRow>> ItemSummaryByDivDeptFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        // HOStock is point-in-time at snapshot run. For the (Division, Department)
        // aggregate we take MAX HOStock per item then SUM across the items in
        // the group so an item's stock isn't double-counted across multiple days.
        var rows = await c.QueryAsync<ItemSummaryByDivDeptRow>(new CommandDefinition(@"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode, MAX(Division) AS Division, MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT Division, Department,
                   SUM(MissingQty) AS MissingQty,
                   SUM(ExcessQty)  AS ExcessQty,
                   SUM(HOStock)    AS HOStock
              FROM itemAgg
             GROUP BY Division, Department
             ORDER BY Division, Department",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ItemSummaryReportRow>> ItemSummaryFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<ItemSummaryReportRow>(new CommandDefinition(@"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode,
                       MAX(ItemName)   AS ItemName,
                       MAX(Division)   AS Division,
                       MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT ItemCode, ItemName, Division, Department,
                   MissingQty, ExcessQty, HOStock
              FROM itemAgg
             ORDER BY ItemCode",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Simcountry FROM bfldata.dbo.datasettings WHERE Simcountry IS NOT NULL ORDER BY Simcountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>
    /// Division list for the PO Counting Report's Division filter — every
    /// distinct Division in Datareporting.dbo.subclassmaster, excluding the
    /// "DATA MIGRATION -D" placeholder value.
    /// </summary>
    public async Task<List<string>> GetPoCountingDivisionsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Division FROM Datareporting.dbo.subclassmaster
             WHERE Division IS NOT NULL AND Division <> '' AND Division <> 'DATA MIGRATION -D'
             ORDER BY Division",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>
    /// Supplier list for the PO Counting Report's Supplier filter — every
    /// distinct SuppName across BuildingCompletionSumm joined to
    /// BuildingCompletionDet_OraPONo on ContNo (the same pair of tables the
    /// report itself reads), not limited to whatever's in the currently
    /// loaded/filtered grid.
    /// </summary>
    public async Task<List<string>> GetPoCountingSuppliersAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT a.SuppName
              FROM BFLDATA.dbo.BuildingCompletionSumm a WITH (NOLOCK)
              JOIN BFLDATA.dbo.BuildingCompletionDet_OraPONo b WITH (NOLOCK) ON a.ContNo = b.ContNo
             WHERE a.SuppName IS NOT NULL AND a.SuppName <> ''
             ORDER BY a.SuppName",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // 4-4-5 retail calendar: 13-week quarters split 4/4/5 weeks per "month". Given any
    // week number, returns every week number sharing that week's 4-4-5 month bucket
    // (e.g. week 31 -> [31,32,33,34]). Used to sum tmpPlanningTarget's per-week Target
    // into a month total without needing calendar dates.
    private static IReadOnlyList<int> WeeksInSameMonth(int week)
    {
        var quarterIndex  = (week - 1) / 13;
        var weekInQuarter = (week - 1) % 13 + 1;
        int monthStartInQuarter, monthLen;
        if (weekInQuarter <= 4)      { monthStartInQuarter = 1; monthLen = 4; }
        else if (weekInQuarter <= 8) { monthStartInQuarter = 5; monthLen = 4; }
        else                         { monthStartInQuarter = 9; monthLen = 5; }
        var quarterBase = quarterIndex * 13;
        return Enumerable.Range(quarterBase + monthStartInQuarter, monthLen).ToList();
    }

    /// <summary>
    /// Merch Need (Month/Week/Day) for a country and selected week, from
    /// LPMSIM.dbo.tmpPlanningTarget (Country, Week, Division, Target). Read via
    /// OnPremBackup like LPMSIM_Batch — no per-country connection-string dance needed.
    /// Week reads that week's Target sum; Day divides it by daysInWeek (7, unless the
    /// selected week is the year's truncated final week); Month sums across the 4-4-5
    /// month bucket containing the selected week.
    /// </summary>
    public async Task<MerchNeedRow> GetMerchNeedAsync(string country, int week, int daysInWeek = 7, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var monthWeeks = WeeksInSameMonth(week);
        var row = await c.QuerySingleOrDefaultAsync<MerchNeedRow>(new CommandDefinition(@"
            SELECT MerchNeedMonth = CAST(ROUND(ISNULL(SUM(CASE WHEN Week IN @monthWeeks THEN Target ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedWeek  = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN Target ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedDay   = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN Target ELSE 0 END), 0) / @daysInWeek, 0) AS BIGINT)
              FROM LPMSIM.dbo.tmpPlanningTarget
             WHERE Country = @country AND Week IN @monthWeeks",
            new { country, week, monthWeeks, daysInWeek }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return row ?? new MerchNeedRow(0, 0, 0);
    }

    /// <summary>Merch Need (Month/Week/Day) per Division for a country and selected week,
    /// from LPMSIM.dbo.tmpPlanningTarget. DivCode is always 0 — this table has no DivCode
    /// column, only a Division name, and callers match on Division name, not DivCode.</summary>
    public async Task<List<MerchNeedDivisionRow>> GetMerchNeedByDivisionAsync(string country, int week, int daysInWeek = 7, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var monthWeeks = WeeksInSameMonth(week);
        var rows = await c.QueryAsync<MerchNeedDivisionRow>(new CommandDefinition(@"
            SELECT DivCode = 0, Division,
                   MerchNeedMonth = CAST(ROUND(ISNULL(SUM(CASE WHEN Week IN @monthWeeks THEN Target ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedWeek  = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN Target ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedDay   = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN Target ELSE 0 END), 0) / @daysInWeek, 0) AS BIGINT)
              FROM LPMSIM.dbo.tmpPlanningTarget
             WHERE Country = @country AND Week IN @monthWeeks
             GROUP BY Division",
            new { country, week, monthWeeks, daysInWeek }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Counting Completion Report (Summary) =====================
    /// <summary>
    /// Reads BFLDATA.dbo.BuildingCompletionSumm — grain is one row per ContNo x
    /// Division, with Country stored directly on the table. LPM Months and Brands
    /// come from a separate detail table, BFLDATA.dbo.BuildingCompletionDet
    /// (LPMDT / Brand columns), correlated by ContNo. Output is one row per
    /// (Country, ContNo), with LPM Months / Divisions / Brands comma-joined as
    /// distinct values (STUFF/FOR XML PATH instead of STRING_AGG(DISTINCT ...) —
    /// the latter needs SQL Server 2022+/compat level 160, not guaranteed here).
    ///
    /// Materialized into #CCBase / #CCDet temp tables (with indexes) rather than
    /// CTEs — a CTE referenced from multiple correlated subqueries gets
    /// re-evaluated on every call, and the original version re-scanned all of
    /// BuildingCompletionDet (unfiltered by date/ContNo) once per output row,
    /// which timed out in production. Same materialize-then-index pattern as
    /// BadBoxesPrefix below.
    ///
    /// Column names confirmed against the live schema (2026-07-15): Country,
    /// ContNo, Trndate (completion date), POnumber (PO number),
    /// CountingStartDate, TotalCheckedQty, Division on BuildingCompletionSumm;
    /// ContNo, LPMDT (date), Brand on BuildingCompletionDet. LPMDT is rendered
    /// as "MMM-yyyy" (e.g. "Jan-2026").
    ///
    /// PurchaseDate (displayed as "Cont-Purchase Date") is the earliest
    /// Trndate on USA.dbo.UsaPurchase for the container (MIN(Trndate) per
    /// ContNo == "TOP 1 ... ORDER BY Trndate ASC"), materialized into
    /// #CCPurchase the same way as #CCDet.
    ///
    /// contNo, when given, narrows to a single container AND skips the date
    /// range filter entirely (search that container across all time) — the
    /// UI treats Container No as a standalone lookup, not an additional
    /// filter on top of the date range.
    /// </summary>
    public async Task<List<CountingCompletionSummaryRow>> GetCountingCompletionSummaryAsync(
        IEnumerable<string>? countries, DateTime fromDate, DateTime toDate, string? contNo,
        string? warehouse = null, CancellationToken ct = default)
    {
        var countryList = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        // null (no argument at all) means "genuinely unrestricted" — an empty-but-non-null
        // list must NOT fall back to "show everything", since that's exactly what a
        // deny-by-default caller passes for a user with zero country grants.
        var noCountryFilter = countries is null;
        var contNoFilter = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim();
        var warehouseFilter = string.IsNullOrWhiteSpace(warehouse) ? null : warehouse.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CountingCompletionSummaryRow>(new CommandDefinition(@"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CCBase')     IS NOT NULL DROP TABLE #CCBase;
            IF OBJECT_ID('tempdb..#CCDet')      IS NOT NULL DROP TABLE #CCDet;
            IF OBJECT_ID('tempdb..#CCPurchase') IS NOT NULL DROP TABLE #CCPurchase;
            IF OBJECT_ID('tempdb..#CCLpm')      IS NOT NULL DROP TABLE #CCLpm;
            IF OBJECT_ID('tempdb..#CCDiv')      IS NOT NULL DROP TABLE #CCDiv;
            IF OBJECT_ID('tempdb..#CCBrand')    IS NOT NULL DROP TABLE #CCBrand;
            IF OBJECT_ID('tempdb..#CCWh')       IS NOT NULL DROP TABLE #CCWh;

            SELECT s.Country,
                   s.ContNo,
                   s.Trndate           AS CountingCompletionDate,
                   s.POnumber          AS PONo,
                   s.CountingStartDate,
                   s.TotalCheckedQty   AS CountedQty,
                   s.Division
              INTO #CCBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CCBase ON #CCBase (Country, ContNo);

            SELECT det.ContNo, det.LPMDT, det.Brand
              INTO #CCDet
              FROM BFLDATA.dbo.BuildingCompletionDet det WITH (NOLOCK)
             WHERE det.ContNo IN (SELECT DISTINCT ContNo FROM #CCBase);

            CREATE CLUSTERED INDEX IX_CCDet ON #CCDet (ContNo);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CCPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CCBase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CCPurchase ON #CCPurchase (ContNo);

            -- LPM Months / Divisions / Brands used to be correlated STUFF+FOR XML PATH
            -- subqueries evaluated once per OUTPUT row (effectively O(rows x detail
            -- rows) — very slow once a date range spans hundreds of containers).
            -- Pre-aggregating each into its own STRING_AGG'd temp table (one row per
            -- key) turns this into a handful of set-based passes plus a cheap join.
            SELECT ContNo, LpmMonths = STRING_AGG(Lbl, ', ') WITHIN GROUP (ORDER BY SortKey)
              INTO #CCLpm
              FROM (SELECT DISTINCT ContNo,
                           Lbl = FORMAT(LPMDT, 'MMM-yyyy'),
                           SortKey = DATEFROMPARTS(YEAR(LPMDT), MONTH(LPMDT), 1)
                      FROM #CCDet WHERE LPMDT IS NOT NULL) x
             GROUP BY ContNo;
            CREATE CLUSTERED INDEX IX_CCLpm ON #CCLpm (ContNo);

            SELECT Country, ContNo, Divisions = STRING_AGG(Division, ', ') WITHIN GROUP (ORDER BY Division)
              INTO #CCDiv
              FROM (SELECT DISTINCT Country, ContNo, Division
                      FROM #CCBase WHERE Division IS NOT NULL AND Division <> '') x
             GROUP BY Country, ContNo;
            CREATE CLUSTERED INDEX IX_CCDiv ON #CCDiv (Country, ContNo);

            SELECT ContNo, Brands = STRING_AGG(Brand, ', ') WITHIN GROUP (ORDER BY Brand)
              INTO #CCBrand
              FROM (SELECT DISTINCT ContNo, Brand
                      FROM #CCDet WHERE Brand IS NOT NULL AND Brand <> '') x
             GROUP BY ContNo;
            CREATE CLUSTERED INDEX IX_CCBrand ON #CCBrand (ContNo);

            -- Warehouse (UAE only: JAFZA/TECHNO) comes from Online.dbo.Photochecking —
            -- a single container can have 100k+ scan rows there, so aggregating (MAX/
            -- DISTINCT) over every matching row per container took 6-13s for a few
            -- hundred containers. TOP 1 via APPLY stops at the first match per
            -- container instead (FORCESEEK — index exists on ContNo, not Warehouse),
            -- which cut this to well under a second for the same containers.
            SELECT b.ContNo, wh.Warehouse
              INTO #CCWh
              FROM (SELECT DISTINCT ContNo FROM #CCBase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CCWh ON #CCWh (ContNo);

            SELECT
                b.Country,
                b.ContNo,
                CountingCompletionDate = MAX(b.CountingCompletionDate),
                PurchaseDate           = MAX(p.PurchaseDate),
                PONo                   = MAX(b.PONo),
                CountingStartDate      = MIN(b.CountingStartDate),
                CountedQty             = SUM(ISNULL(b.CountedQty, 0)),
                LpmMonths              = MAX(lm.LpmMonths),
                Divisions              = MAX(dv.Divisions),
                Brands                 = MAX(br.Brands),
                Warehouse              = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM #CCBase b
              LEFT JOIN #CCPurchase p  ON p.ContNo = b.ContNo
              LEFT JOIN #CCLpm      lm ON lm.ContNo = b.ContNo
              LEFT JOIN #CCDiv      dv ON dv.Country = b.Country AND dv.ContNo = b.ContNo
              LEFT JOIN #CCBrand    br ON br.ContNo = b.ContNo
              LEFT JOIN #CCWh       wh ON wh.ContNo = b.ContNo
             WHERE (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, b.ContNo
             ORDER BY b.Country, CountingCompletionDate;

            DROP TABLE #CCBase, #CCDet, #CCPurchase, #CCLpm, #CCDiv, #CCBrand, #CCWh;",
            new { countries = countryList, noCountryFilter = noCountryFilter ? 1 : 0, contNoFilter, warehouseFilter,
                  from = fromDate.Date, toExclusive = toDate.Date.AddDays(1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Counting Completion Report — Detailed / Allocation-wise. Same columns
    /// as the Summary report (PO No, Counting Start/Completion Date,
    /// Cont-Purchase Date, LPM Months, Divisions, Brands), but broken down
    /// further by PalletType (Box Category) — one row per (Country, ContNo,
    /// PalletType), with BuildQty = SUM(CheckedQty) for that box category.
    /// No ItemCode/ItemName (that's the Item-wise view). Rows with zero
    /// CheckedQty are dropped; ordered by Country, ContNo. contNo, when
    /// given, also skips the date range filter entirely (see
    /// GetCountingCompletionSummaryAsync for why).
    /// </summary>
    public async Task<List<CountingAllocationRow>> GetCountingAllocationAsync(
        IEnumerable<string>? countries, DateTime fromDate, DateTime toDate, string? contNo,
        string? warehouse = null, CancellationToken ct = default)
    {
        var countryList = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        // null (no argument at all) means "genuinely unrestricted" — an empty-but-non-null
        // list must NOT fall back to "show everything", since that's exactly what a
        // deny-by-default caller passes for a user with zero country grants.
        var noCountryFilter = countries is null;
        var contNoFilter = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim();
        var warehouseFilter = string.IsNullOrWhiteSpace(warehouse) ? null : warehouse.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CountingAllocationRow>(new CommandDefinition(@"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CABase')      IS NOT NULL DROP TABLE #CABase;
            IF OBJECT_ID('tempdb..#CAConts')     IS NOT NULL DROP TABLE #CAConts;
            IF OBJECT_ID('tempdb..#CADateBoxes') IS NOT NULL DROP TABLE #CADateBoxes;
            IF OBJECT_ID('tempdb..#CARaw')       IS NOT NULL DROP TABLE #CARaw;
            IF OBJECT_ID('tempdb..#CAUpcs')      IS NOT NULL DROP TABLE #CAUpcs;
            IF OBJECT_ID('tempdb..#CADetUae')    IS NOT NULL DROP TABLE #CADetUae;
            IF OBJECT_ID('tempdb..#CADetOther')  IS NOT NULL DROP TABLE #CADetOther;
            IF OBJECT_ID('tempdb..#CADet')       IS NOT NULL DROP TABLE #CADet;
            IF OBJECT_ID('tempdb..#CAPurchase')  IS NOT NULL DROP TABLE #CAPurchase;
            IF OBJECT_ID('tempdb..#CALpm')       IS NOT NULL DROP TABLE #CALpm;
            IF OBJECT_ID('tempdb..#CADiv')       IS NOT NULL DROP TABLE #CADiv;
            IF OBJECT_ID('tempdb..#CABrand')     IS NOT NULL DROP TABLE #CABrand;
            IF OBJECT_ID('tempdb..#CAWh')        IS NOT NULL DROP TABLE #CAWh;

            SELECT s.Country,
                   s.ContNo,
                   s.Trndate           AS CountingCompletionDate,
                   s.POnumber          AS PONo,
                   s.CountingStartDate,
                   s.Division
              INTO #CABase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CABase ON #CABase (Country, ContNo);

            -- Hybrid: UAE uses USA.dbo.UPCBoxDet/UPCBoxHead/UPCBarCodes (per explicit
            -- request, optimized — see history), every other country uses the
            -- original BFLDATA.dbo.BuildingCompletionDet. UPCBoxDet doesn't have box
            -- data for every container (e.g. KSA's SAINT5900/SAINT7243 had zero rows
            -- there despite being valid, counted containers), so non-UAE stays on the
            -- source that actually covers it.
            SELECT DISTINCT ContNo INTO #CAConts FROM #CABase WHERE Country = 'UAE';
            CREATE UNIQUE CLUSTERED INDEX IX_CAConts ON #CAConts (ContNo);

            SELECT h.BoxNo, h.PalletType, h.LPMDt, h.OraPoNo,
                   ContNo = LEFT(h.BoxNo, CHARINDEX('-', h.BoxNo) - 1)
              INTO #CADateBoxes
              FROM USA.dbo.UPCBoxHead h WITH (NOLOCK)
             WHERE ((@contNoFilter IS NULL AND h.TrnDate >= @from AND h.TrnDate < @toExclusive)
                    OR (@contNoFilter IS NOT NULL AND h.BoxNo LIKE @contNoFilter + '-%'))
               AND CHARINDEX('-', h.BoxNo) > 0;
            CREATE CLUSTERED INDEX IX_CADateBoxes ON #CADateBoxes (BoxNo);
            CREATE NONCLUSTERED INDEX IX_CADateBoxes_ContNo ON #CADateBoxes (ContNo);

            SELECT dfb.ContNo, dfb.PalletType AS Pallettype, v.Qty AS CheckedQty,
                   dfb.LPMDt AS LPMDT, v.UPC
              INTO #CARaw
              FROM #CAConts b
              JOIN #CADateBoxes dfb ON dfb.ContNo = b.ContNo
              JOIN USA.dbo.UPCBoxDet v WITH (NOLOCK) ON v.BoxNo = dfb.BoxNo;

            SELECT DISTINCT UPC INTO #CAUpcs FROM #CARaw WHERE UPC IS NOT NULL AND UPC <> '';
            CREATE UNIQUE CLUSTERED INDEX IX_CAUpcs ON #CAUpcs (UPC);

            SELECT r.ContNo, r.Pallettype, r.CheckedQty, r.LPMDT, bc2.Vendor AS Brand
              INTO #CADetUae
              FROM #CARaw r
              LEFT JOIN #CAUpcs u ON u.UPC = r.UPC
              LEFT JOIN USA.dbo.UPCBarCodes bc2 WITH (NOLOCK) ON bc2.UPC = u.UPC;

            SELECT det.ContNo, det.Pallettype, det.CheckedQty, det.LPMDT, det.Brand
              INTO #CADetOther
              FROM BFLDATA.dbo.BuildingCompletionDet det WITH (NOLOCK)
             WHERE det.ContNo IN (SELECT DISTINCT ContNo FROM #CABase WHERE Country <> 'UAE');

            SELECT * INTO #CADet FROM (
                SELECT * FROM #CADetUae
                UNION ALL
                SELECT * FROM #CADetOther
            ) x;
            CREATE CLUSTERED INDEX IX_CADet ON #CADet (ContNo, Pallettype);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CAPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CABase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CAPurchase ON #CAPurchase (ContNo);

            -- LPM Months / Divisions / Brands used to be correlated STUFF+FOR XML PATH
            -- subqueries evaluated once per OUTPUT row (Country x ContNo x PalletType) —
            -- pre-aggregating each into its own STRING_AGG'd temp table (one row per key)
            -- turns this into a handful of set-based passes plus a cheap join instead.
            SELECT ContNo, PalletType = ISNULL(Pallettype, '(none)'),
                   LpmMonths = STRING_AGG(Lbl, ', ') WITHIN GROUP (ORDER BY SortKey)
              INTO #CALpm
              FROM (SELECT DISTINCT ContNo, Pallettype,
                           Lbl = FORMAT(LPMDT, 'MMM-yyyy'),
                           SortKey = DATEFROMPARTS(YEAR(LPMDT), MONTH(LPMDT), 1)
                      FROM #CADet WHERE LPMDT IS NOT NULL) x
             GROUP BY ContNo, ISNULL(Pallettype, '(none)');
            CREATE CLUSTERED INDEX IX_CALpm ON #CALpm (ContNo, PalletType);

            SELECT Country, ContNo, Divisions = STRING_AGG(Division, ', ') WITHIN GROUP (ORDER BY Division)
              INTO #CADiv
              FROM (SELECT DISTINCT Country, ContNo, Division
                      FROM #CABase WHERE Division IS NOT NULL AND Division <> '') x
             GROUP BY Country, ContNo;
            CREATE CLUSTERED INDEX IX_CADiv ON #CADiv (Country, ContNo);

            SELECT ContNo, PalletType = ISNULL(Pallettype, '(none)'),
                   Brands = STRING_AGG(Brand, ', ') WITHIN GROUP (ORDER BY Brand)
              INTO #CABrand
              FROM (SELECT DISTINCT ContNo, Pallettype, Brand
                      FROM #CADet WHERE Brand IS NOT NULL AND Brand <> '') x
             GROUP BY ContNo, ISNULL(Pallettype, '(none)');
            CREATE CLUSTERED INDEX IX_CABrand ON #CABrand (ContNo, PalletType);

            -- Warehouse (UAE only: JAFZA/TECHNO) comes from Online.dbo.Photochecking —
            -- see #CCWh in GetCountingCompletionSummaryAsync for why TOP 1/APPLY (not
            -- MAX/DISTINCT) is used here.
            SELECT b.ContNo, wh.Warehouse
              INTO #CAWh
              FROM (SELECT DISTINCT ContNo FROM #CABase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CAWh ON #CAWh (ContNo);

            SELECT
                b.Country,
                b.ContNo,
                CountingCompletionDate = MAX(b.CountingCompletionDate),
                PurchaseDate           = MAX(p.PurchaseDate),
                PONo                   = MAX(b.PONo),
                CountingStartDate      = MIN(b.CountingStartDate),
                PalletType             = ISNULL(d.Pallettype, '(none)'),
                TypeName               = MAX(pt.TypeName),
                BuildQty               = SUM(ISNULL(d.CheckedQty, 0)),
                LpmMonths              = MAX(lm.LpmMonths),
                Divisions              = MAX(dv.Divisions),
                Brands                 = MAX(br.Brands),
                Warehouse              = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM #CABase b
              JOIN #CADet d ON d.ContNo = b.ContNo
              LEFT JOIN #CAPurchase p  ON p.ContNo = b.ContNo
              LEFT JOIN BFLDATA.dbo.PalletType pt WITH (NOLOCK) ON pt.PalletType = d.Pallettype
              LEFT JOIN #CALpm      lm ON lm.ContNo = b.ContNo AND lm.PalletType = ISNULL(d.Pallettype, '(none)')
              LEFT JOIN #CADiv      dv ON dv.Country = b.Country AND dv.ContNo = b.ContNo
              LEFT JOIN #CABrand    br ON br.ContNo = b.ContNo AND br.PalletType = ISNULL(d.Pallettype, '(none)')
              LEFT JOIN #CAWh       wh ON wh.ContNo = b.ContNo
             WHERE (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, b.ContNo, ISNULL(d.Pallettype, '(none)')
            HAVING SUM(ISNULL(d.CheckedQty, 0)) > 0
             ORDER BY b.Country, b.ContNo;

            DROP TABLE #CABase, #CAConts, #CADateBoxes, #CARaw, #CAUpcs, #CADetUae, #CADetOther, #CADet, #CAPurchase, #CALpm, #CADiv, #CABrand, #CAWh;",
            new { countries = countryList, noCountryFilter = noCountryFilter ? 1 : 0, contNoFilter, warehouseFilter,
                  from = fromDate.Date, toExclusive = toDate.Date.AddDays(1) },
            // TrnDate-first filtering (see comment above) brought the 14-container
            // case down to ~2.5s, but the UAE branch's final Brand join still scales
            // with distinct UPC count — verified ~39s for a full 30-day UAE range
            // (~494k rows, ~98k distinct UPCs). 300s leaves real headroom for wider
            // ranges than tested rather than risk a client-side timeout/hang.
            commandTimeout: 300, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Counting Completion Report — Detailed. One row per (Country, ContNo,
    /// ItemCode, PalletType) from BFLDATA.dbo.BuildingCompletionDet — Box
    /// Category (Pallettype), Item Code/Name, and Qty (CheckedQty) all come
    /// straight off that table. Division is the item's own division (from
    /// Datareporting.dbo.vUPC_SUBCLASS via ItemCode = upc) — unlike
    /// Summary/Allocation-wise, this view is item-level so the container's
    /// full Divisions list would be misleading here. contNo, when given,
    /// narrows to a single container and skips the date range filter
    /// entirely (see GetCountingCompletionSummaryAsync for why). Rows with
    /// zero CheckedQty (not actually counted) are dropped; the rest are
    /// ordered by Country, ContNo.
    /// </summary>
    public async Task<List<CountingCompletionDetailRow>> GetCountingDetailAsync(
        IEnumerable<string>? countries, DateTime fromDate, DateTime toDate, string? contNo,
        string? warehouse = null, CancellationToken ct = default)
    {
        var countryList = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        // null (no argument at all) means "genuinely unrestricted" — an empty-but-non-null
        // list must NOT fall back to "show everything", since that's exactly what a
        // deny-by-default caller passes for a user with zero country grants.
        var noCountryFilter = countries is null;
        var contNoFilter = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim();
        var warehouseFilter = string.IsNullOrWhiteSpace(warehouse) ? null : warehouse.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CountingCompletionDetailRow>(new CommandDefinition(@"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CDBase')     IS NOT NULL DROP TABLE #CDBase;
            IF OBJECT_ID('tempdb..#CDPurchase') IS NOT NULL DROP TABLE #CDPurchase;
            IF OBJECT_ID('tempdb..#CDWh')       IS NOT NULL DROP TABLE #CDWh;

            SELECT DISTINCT s.Country, s.ContNo
              INTO #CDBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CDBase ON #CDBase (ContNo);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CDPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CDBase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CDPurchase ON #CDPurchase (ContNo);

            -- Warehouse (UAE only: JAFZA/TECHNO) comes from Online.dbo.Photochecking —
            -- see #CCWh in GetCountingCompletionSummaryAsync for why TOP 1/APPLY (not
            -- MAX/DISTINCT) is used here.
            SELECT b.ContNo, wh.Warehouse
              INTO #CDWh
              FROM (SELECT DISTINCT ContNo FROM #CDBase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CDWh ON #CDWh (ContNo);

            SELECT
                b.Country,
                d.ContNo,
                PurchaseDate = MAX(p.PurchaseDate),
                PalletType   = ISNULL(d.Pallettype, '(none)'),
                TypeName     = MAX(pt.TypeName),
                ItemCode     = d.upc,
                ItemName     = MAX(d.itemname),
                Qty          = SUM(ISNULL(d.CheckedQty, 0)),
                LpmMonths    = FORMAT(MAX(d.LPMDT), 'MMM-yyyy'),
                Division     = MAX(sub.Division),
                Brand        = MAX(d.Brand),
                Warehouse    = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM BFLDATA.dbo.BuildingCompletionDet d WITH (NOLOCK)
              JOIN #CDBase b ON b.ContNo = d.ContNo
              LEFT JOIN #CDPurchase p ON p.ContNo = d.ContNo
              LEFT JOIN Datareporting.dbo.vUPC_SUBCLASS sub WITH (NOLOCK) ON sub.itemcode = d.upc
              LEFT JOIN BFLDATA.dbo.PalletType pt WITH (NOLOCK) ON pt.PalletType = d.Pallettype
              LEFT JOIN #CDWh wh ON wh.ContNo = d.ContNo
             WHERE d.ContNo IN (SELECT DISTINCT ContNo FROM #CDBase)
               AND (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, d.ContNo, d.upc, ISNULL(d.Pallettype, '(none)')
            HAVING SUM(ISNULL(d.CheckedQty, 0)) > 0
             ORDER BY b.Country, d.ContNo;

            DROP TABLE #CDBase, #CDPurchase, #CDWh;",
            new { countries = countryList, noCountryFilter = noCountryFilter ? 1 : 0, contNoFilter, warehouseFilter,
                  from = fromDate.Date, toExclusive = toDate.Date.AddDays(1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== PO Counting Report =====================
    /// <summary>
    /// PO Counting Report — one row per (Country, ContNo, PONumber), splitting
    /// the container-grain BuildingCompletionSumm data down to real PO grain
    /// via BFLDATA.dbo.BuildingCompletionDet_OraPONo (one OraPONo per item
    /// row, aggregated per container). CountingCompletionDate/Division/
    /// Supplier come off BuildingCompletionSumm and are the same for every PO
    /// in a given container (that table doesn't split them per-PO). contNo
    /// and poNumber, like the ContNo lookup in the Counting Completion
    /// Report, each skip the date-range filter when given — a standalone
    /// lookup across all time rather than an additional filter on top of the
    /// date range.
    /// </summary>
    public async Task<List<PoCountingRow>> GetPoCountingAsync(
        IEnumerable<string>? countries, DateTime fromDate, DateTime toDate,
        string? contNo, string? poNumber, CancellationToken ct = default)
    {
        var countryList = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        // null (no argument at all) means "genuinely unrestricted" — an empty-but-non-null
        // list must NOT fall back to "show everything", since that's exactly what a
        // deny-by-default caller passes for a user with zero country grants.
        var noCountryFilter = countries is null;
        var contNoFilter = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim();
        var poFilter = string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim();
        var skipDateFilter = contNoFilter is not null || poFilter is not null;

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<PoCountingRow>(new CommandDefinition(@"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#PCBase') IS NOT NULL DROP TABLE #PCBase;
            IF OBJECT_ID('tempdb..#PCDet')  IS NOT NULL DROP TABLE #PCDet;

            SELECT s.Country, s.ContNo, s.Trndate AS CountingCompletionDate,
                   s.Division, s.suppname AS Supplier,
                   s.Purchasetype AS PurchaseType, s.remarks AS Remarks,
                   s.status AS Status, s.buyer AS Buyer
              INTO #PCBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@skipDateFilter = 1 OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_PCBase ON #PCBase (ContNo);

            SELECT d.ContNo, d.OraPONo,
                   OrderSheetQty   = SUM(ISNULL(d.Qty, 0)),
                   GRNQty          = SUM(ISNULL(d.CheckedQty, 0)),
                   MissingQty      = SUM(ISNULL(d.MissingQty, 0)),
                   ExcessQty       = SUM(ISNULL(d.ExcessQty, 0)),
                   ReturnToSuppQty = SUM(ISNULL(d.ReturnToSuppQty, 0)),
                   ReturnToBuyQty  = SUM(ISNULL(d.ReturnToBuyQty, 0))
              INTO #PCDet
              FROM BFLDATA.dbo.BuildingCompletionDet_OraPONo d WITH (NOLOCK)
             WHERE d.ContNo IN (SELECT ContNo FROM #PCBase)
               AND (@poFilter IS NULL OR d.OraPONo = @poFilter)
             GROUP BY d.ContNo, d.OraPONo;

            CREATE CLUSTERED INDEX IX_PCDet ON #PCDet (ContNo, OraPONo);

            -- Real Order Qty (as opposed to OrderSheetQty, which is really just the
            -- Det_OraPONo item total) comes from HODATA.dbo.Vusaorder — the actual
            -- purchase-order line data — summed per (ContNo, ORAPONo). Vusaorder.refno
            -- is the container number (verified against BuildingCompletionSumm.ContNo),
            -- not Vusaorder.Contno (which holds a Ref_ prefixed value, a job/reference
            -- number, or blank depending on the row) — refno is the correct join key.
            SELECT vo.refno AS ContNo, vo.ORAPONo, OrderQty = SUM(ISNULL(vo.Qty, 0))
              INTO #PCOrder
              FROM HODATA.dbo.Vusaorder vo WITH (NOLOCK)
             WHERE vo.refno IN (SELECT DISTINCT ContNo FROM #PCBase)
               AND vo.ORAPONo IN (SELECT DISTINCT OraPONo FROM #PCDet)
             GROUP BY vo.refno, vo.ORAPONo;

            CREATE CLUSTERED INDEX IX_PCOrder ON #PCOrder (ContNo, ORAPONo);

            SELECT
                b.Country,
                b.ContNo,
                PONumber                = det.OraPONo,
                CountingCompletionDate  = b.CountingCompletionDate,
                b.Division,
                b.Supplier,
                OrderSheetQty           = det.OrderSheetQty,
                OrderQty                = ISNULL(ord.OrderQty, 0),
                GRNQty                  = det.GRNQty,
                ContainerFillRate       = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.GRNQty * 100.0 / det.OrderSheetQty, 2) END,
                MissingQty              = det.MissingQty,
                PctMissing              = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.MissingQty * 100.0 / det.OrderSheetQty, 2) END,
                ExcessQty               = det.ExcessQty,
                PctExcess               = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.ExcessQty  * 100.0 / det.OrderSheetQty, 2) END,
                ReturnToSuppQty         = det.ReturnToSuppQty,
                PctMissingReturn        = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.ReturnToSuppQty * 100.0 / det.OrderSheetQty, 2) END,
                ReturnToBuyQty          = det.ReturnToBuyQty,
                -- ContErrorUnits at container level is just TotalMissingQty + TotalExcessQty
                -- (verified: BuildingCompletionSumm.ContErrorUnits == SUM of both across that
                -- container's POs), so compute it per-PO directly from this same #PCDet row
                -- instead of splitting the container total proportionally by GRNQty — that
                -- proportional split doesn't reconcile with each PO's own Missing/Excess Qty.
                ErrorUnits              = det.MissingQty + det.ExcessQty,
                ErrorRate               = CASE WHEN det.OrderSheetQty = 0 THEN 0
                                          ELSE ROUND((det.MissingQty + det.ExcessQty) * 100.0 / det.OrderSheetQty, 2) END,
                b.PurchaseType,
                b.Remarks,
                b.Status,
                b.Buyer
              FROM #PCBase b
              JOIN #PCDet det ON det.ContNo = b.ContNo
              LEFT JOIN #PCOrder ord ON ord.ContNo = det.ContNo AND ord.ORAPONo = det.OraPONo
             ORDER BY b.Country, b.ContNo, det.OraPONo;

            DROP TABLE #PCBase, #PCDet, #PCOrder;",
            new { countries = countryList, noCountryFilter = noCountryFilter ? 1 : 0, contNoFilter, poFilter,
                  skipDateFilter = skipDateFilter ? 1 : 0,
                  from = fromDate.Date, toExclusive = toDate.Date.AddDays(1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// PO Counting Report — Detailed view. Same filters as GetPoCountingAsync
    /// (Country/date-range/ContNo/PO Number), but one row per item instead of
    /// aggregated per PO — the "Detailed" report option, same idea as the
    /// Counting Completion Report's Summary/Detailed toggle.
    /// </summary>
    public async Task<List<PoCountingItemRow>> GetPoCountingDetailAsync(
        IEnumerable<string>? countries, DateTime fromDate, DateTime toDate,
        string? contNo, string? poNumber, CancellationToken ct = default)
    {
        var countryList = countries?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
        var noCountryFilter = countries is null;
        var contNoFilter = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim();
        var poFilter = string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim();
        var skipDateFilter = contNoFilter is not null || poFilter is not null;

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<PoCountingItemRow>(new CommandDefinition(@"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#PDBase') IS NOT NULL DROP TABLE #PDBase;

            SELECT DISTINCT s.ContNo
              INTO #PDBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@skipDateFilter = 1 OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE UNIQUE CLUSTERED INDEX IX_PDBase ON #PDBase (ContNo);

            SELECT d.ContNo,
                   PONumber   = d.OraPONo,
                   ItemCode   = d.upc,
                   ItemName   = d.itemname,
                   Style      = d.style,
                   PalletType = d.Pallettype,
                   Brand      = d.Brand,
                   LpmDt      = d.LPMDt,
                   Qty              = ISNULL(d.Qty, 0),
                   CheckedQty       = ISNULL(d.CheckedQty, 0),
                   MissingQty       = ISNULL(d.MissingQty, 0),
                   ExcessQty        = ISNULL(d.ExcessQty, 0),
                   ReturnToSuppQty  = ISNULL(d.ReturnToSuppQty, 0),
                   ReturnToBuyQty   = ISNULL(d.ReturnToBuyQty, 0)
              FROM BFLDATA.dbo.BuildingCompletionDet_OraPONo d WITH (NOLOCK)
             WHERE d.ContNo IN (SELECT ContNo FROM #PDBase)
               AND (@poFilter IS NULL OR d.OraPONo = @poFilter)
             ORDER BY d.ContNo, d.OraPONo, d.upc;

            DROP TABLE #PDBase;",
            new { countries = countryList, noCountryFilter = noCountryFilter ? 1 : 0, contNoFilter, poFilter,
                  skipDateFilter = skipDateFilter ? 1 : 0,
                  from = fromDate.Date, toExclusive = toDate.Date.AddDays(1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // SQL prefix that materialises #BadBoxes — must be prepended to whatever
    // query references the temp table, so the build + read happen in the
    // SAME Dapper command (= same SQL Server session). Splitting it across
    // two ExecuteAsync calls dropped the temp table between commands in
    // testing ("Invalid object name '#BadBoxes'").
    private const string BadBoxesPrefix = @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#BadBoxes') IS NOT NULL DROP TABLE #BadBoxes;
        SELECT DISTINCT
            cr.Palletno  AS BoxNo,
            cr.Trndate   AS ClosedDt,
            cr.closedby  AS ClosedBy,
            ISNULL(cr.missqty,0) AS MissQty,
            ISNULL(cr.zeroqty,0) AS ExcessQty
          INTO #BadBoxes
          FROM bfldata.dbo.CloseR1pallet cr WITH (NOLOCK)
         WHERE cr.Trndate >= @from AND cr.Trndate <= @to
           AND ISNULL(cr.missqty,0) + ISNULL(cr.zeroqty,0) > 0
           AND EXISTS (
               SELECT 1 FROM usa.dbo.AMEChecking a WITH (NOLOCK)
                WHERE a.contno = cr.Palletno AND a.Trndate >= @from);
        CREATE CLUSTERED INDEX IX_BadBoxes ON #BadBoxes (BoxNo);
        ";

    /// <summary>Box Summary — one row per (BoxNo, ClosedDt, ClosedBy).</summary>
    public async Task<List<BoxSummaryRow>> BoxSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxSummaryRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT BoxNo, ClosedDt, ClosedBy,
                   SUM(MissQty)   AS MissQty,
                   SUM(ExcessQty) AS ExcessQty
              FROM #BadBoxes
             GROUP BY BoxNo, ClosedDt, ClosedBy
             ORDER BY ClosedBy DESC, ClosedDt DESC",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Summary aggregated by month (yyyy-MM) — UI table.</summary>
    public async Task<List<BoxSummaryMonthRow>> BoxSummaryByMonthAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxSummaryMonthRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT CONVERT(varchar(7), ClosedDt, 120)  AS [Month],
                   COUNT(DISTINCT BoxNo)               AS BoxCount,
                   SUM(MissQty)                        AS MissQty,
                   SUM(ExcessQty)                      AS ExcessQty
              FROM #BadBoxes
             GROUP BY CONVERT(varchar(7), ClosedDt, 120)
             ORDER BY [Month]",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Detail combined — Missing + Excess rows tagged with a Type column.</summary>
    public async Task<List<BoxDetailCombinedRow>> BoxDetailCombinedAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxDetailCombinedRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT d.BoxNo, d.preparedby AS PreparedBy, d.itemcode AS ItemCode,
                   d.qty AS Qty, d.QtyIssued AS QtyIssued,
                   CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                        THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                   CASE WHEN ISNULL(d.Status,'') <> ''
                        THEN d.QtyIssued          ELSE 0 END AS ExcessQty
              FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
              INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
             WHERE (ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty)
                OR (ISNULL(d.Status,'') <> '' AND d.QtyIssued > 0)
             ORDER BY d.BoxNo, d.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Item Summary aggregated by (Division, Department) — UI table.</summary>
    public async Task<List<ItemSummaryByDivDeptRow>> ItemSummaryByDivDeptAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<ItemSummaryByDivDeptRow>(new CommandDefinition(BadBoxesPrefix + @"
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN d.QtyIssued          ELSE 0 END AS ExcessQty
                  FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                  INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
            ), agg AS (
                SELECT itemcode, SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty
                  FROM base
                 GROUP BY itemcode
                HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            ), soh AS (
                SELECT itemcode, SUM(soh) AS HOStock
                  FROM racks.dbo.lpm_locstock WITH (NOLOCK)
                 WHERE itemcode IN (SELECT itemcode FROM agg)
                 GROUP BY itemcode
            )
            SELECT sub.Division                AS Division,
                   sub.Department              AS Department,
                   SUM(a.MissingQty)           AS MissingQty,
                   SUM(a.ExcessQty)            AS ExcessQty,
                   SUM(ISNULL(s.HOStock, 0))   AS HOStock
              FROM agg a
              LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
              LEFT JOIN soh s                                              ON s.itemcode  = a.itemcode
             GROUP BY sub.Division, sub.Department
             ORDER BY sub.Division, sub.Department",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Item Summary — per ItemCode totals of missing + excess, joined with item name
    /// (hodata.itemmaster), hierarchy/division/department (datareporting.vupc_subclass),
    /// and HO stock (racks.lpm_locstock).
    /// </summary>
    // ===================== Production Summary report (ported from LPMSIM) =====================
    /// <summary>
    /// Ported from LPMSIM (ProductionCheckingReportService.GetAsync, UAE path only).
    /// Reads usa.dbo.amechecking scans against LPMSIM.dbo.LPMSIM_Batch country filter
    /// + Sources-derived Kind, joins Datareporting for Division, and returns three
    /// result sets: detailed rows / summary rows / overall store qty. Transfer Qty
    /// is fetched separately from bfldata.dbo.DailyCountCategoryTrf; see
    /// GetTransferQtyAsync for the UAE-only Warehouse filter and its caveat for
    /// non-UAE countries (no Country column exists, so the figure returned is the
    /// UAE-wide total, not that country's own).
    /// </summary>
    public async Task<ProductionCheckingResult> GetProductionCheckingAsync(
        string country, DateTime fromDate, DateTime toDateInclusive, CancellationToken ct = default)
    {
        // 1.14.268 (LPMSIM) — for any non-UAE country with a {Country}_DB_ConnectionString
        // configured, read scans directly from that server and bulk-copy them onto the
        // central UAE backup connection for the LPMSIM_Batch / Datareporting enrichment.
        if (!string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
        {
            string? cs;
            try { cs = resolver.GetCountryConnectionString(country); }
            catch { cs = null; }
            if (string.IsNullOrWhiteSpace(cs))
            {
                // No per-country connection configured for this country at all, so there's
                // no server to read its own bfldata.dbo.DailyCountCategoryTrf from either.
                // Best-effort fallback: central OnPremBackup's copy (UAE-wide, not this country's).
                await using var backupConn = OpenOnPremBackup();
                var noScanTransferQty = await GetTransferQtyAsync(backupConn, uaeOnly: false, fromDate, toDateInclusive, ct);
                return new ProductionCheckingResult(new(), new(), 0, noScanTransferQty, new());
            }
            return await GetProductionCheckingViaConnStringAsync(country, cs, fromDate, toDateInclusive, ct);
        }

        // Production day = WH-shift window [D 06:00 GST, D+1 06:00 GST). Scans
        // before 06:00 on calendar date D count toward D-1's shift.
        var fromInclusive       = fromDate.Date.AddHours(6);
        var toExclusive         = toDateInclusive.Date.AddDays(1).AddHours(6);
        var fromDateOnly        = fromDate.Date;
        var toDateExclusiveOnly = toDateInclusive.Date.AddDays(2);

        var rows      = new List<ProductionCheckingRow>();
        var summary   = new List<ProductionCheckingSummaryRow>();
        var ex2Shops  = new List<Ex2ShopRow>();
        int overallStoreQty = 0;

        await using var conn = OpenOnPremBackup();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = @"
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Scans')     IS NOT NULL DROP TABLE #Scans;
IF OBJECT_ID('tempdb..#BatchKind') IS NOT NULL DROP TABLE #BatchKind;
IF OBJECT_ID('tempdb..#ItemDiv')   IS NOT NULL DROP TABLE #ItemDiv;

-- 1) Materialize the amechecking slice ONCE.
SELECT
    BatchNo = CASE
                  WHEN CHARINDEX('BP(', c.CmpName) > 0
                  THEN TRY_CAST(SUBSTRING(c.CmpName,
                                          CHARINDEX('BP(', c.CmpName) + 3,
                                          CHARINDEX(')',  c.CmpName, CHARINDEX('BP(', c.CmpName))
                                          - CHARINDEX('BP(', c.CmpName) - 3) AS bigint)
                  ELSE NULL
              END,
    Itemcode = ISNULL(c.Itemcode, ''),
    ShopName = ISNULL(c.ShopName, ''),
    Contno   = ISNULL(c.Contno,   ''),
    Result   = ISNULL(c.Result, -1),
    ProductionDay = CAST(CASE
                             WHEN TRY_CAST(c.Time1 AS time) >= '06:00:00'
                                 THEN c.TrnDate
                             ELSE DATEADD(day, -1, c.TrnDate)
                         END AS date)
  INTO #Scans
  FROM usa.dbo.amechecking c
 WHERE c.TrnDate >= @fromDateOnly
   AND c.TrnDate <  @toDateExclusiveOnly
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) >= @from
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) <  @toExclusive;

CREATE CLUSTERED INDEX IX_Scans ON #Scans (BatchNo, Itemcode);

-- 2) Country gate. Delete wrong-country batches; keep orphans & nulls as Unknown.
DELETE s
  FROM #Scans s
  INNER JOIN LPMSIM.dbo.LPMSIM_Batch b ON b.LPMBatchNo = s.BatchNo
 WHERE b.Country <> @country;

-- 3) Per-BATCH Kind from LPMSIM_Batch.Sources.
SELECT
    b.LPMBatchNo,
    Kind = CASE
               WHEN b.Sources LIKE '%Non-LPM%'
                AND REPLACE(b.Sources, 'Non-LPM', '') LIKE '%LPM%' THEN 'Mixed'
               WHEN b.Sources LIKE '%Non-LPM%' THEN 'Non-LPM'
               WHEN b.Sources LIKE '%LPM%'     THEN 'LPM'
               ELSE 'Unknown'
           END
  INTO #BatchKind
  FROM LPMSIM.dbo.LPMSIM_Batch b
 WHERE b.LPMBatchNo IN (SELECT DISTINCT BatchNo FROM #Scans WHERE BatchNo IS NOT NULL);

CREATE CLUSTERED INDEX IX_BatchKind ON #BatchKind (LPMBatchNo);

-- 4) Division lookup.
SELECT u.itemcode,
       Division = MIN(sm.Division)
  INTO #ItemDiv
  FROM (SELECT DISTINCT Itemcode FROM #Scans WHERE Itemcode <> '') si
  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = si.Itemcode
  INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
 GROUP BY u.itemcode;

CREATE CLUSTERED INDEX IX_ItemDiv ON #ItemDiv (itemcode);

-- 5) Detailed result set.
SELECT
    s.ProductionDay,
    s.BatchNo,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END)
  FROM #Scans s
  LEFT JOIN #BatchKind bk ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv   idv ON idv.itemcode  = s.Itemcode
 GROUP BY s.ProductionDay, s.BatchNo, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          ISNULL(s.BatchNo, -1) DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 6) Summary result set.
SELECT
    s.ProductionDay,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END),
    UaeStoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'UAE'          THEN 1 ELSE 0 END),
    OmanStoreQty = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Oman'         THEN 1 ELSE 0 END),
    Ex2StoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Ex2Locations' THEN 1 ELSE 0 END),
    Ex2TotalScanned = COUNT_BIG(CASE WHEN ds.SIMCountry = 'Ex2Locations' THEN 1 END)
  FROM #Scans s
  LEFT JOIN #BatchKind         bk  ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv           idv ON idv.itemcode  = s.Itemcode
  LEFT JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = s.ShopName AND s.ShopName <> ''
 GROUP BY s.ProductionDay, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 7) Overall Store Qty scalar.
SELECT OverallStoreQty = SUM(CASE WHEN Result IN (0, 13) THEN 1 ELSE 0 END) FROM #Scans;

-- 8) Per-(country, date) Transfer Qty, from bfldata.dbo.DailyCountCategoryTrf, for
-- every shop-card breakdown shown under UAE (EX2* cards + UAE Shops/Oman Shops) AND
-- the Detailed pivot's UAE/Oman/Ex2 Qty columns (shown once per Date, since Transfer
-- Qty has no Division breakdown to match the pivot's per-Division rows). Two disjoint
-- slices unioned together, since they come from different warehouses and country
-- groupings:
--   - Ex2Locations shops (KSA/QATAR/BAHRAIN/KUWAIT/MALAYSIA — e.g. ShopName
--     BFLP2MYS = MALAYSIA) ship via Warehouse='JAFZA'.
--   - UAE/Oman ship via Warehouse='TECHNO', same warehouse as the overall
--     Transfer Qty scalar (GetTransferQtyAsync) above — this is that same
--     TECHNO total, just split by country/date instead of summed into one figure.
-- Independent of #Scans — reads the transfer table directly.
SELECT ds.Country,
       TrfDate = CAST(d.TrnDate AS DATE),
       TransferQty = ISNULL(SUM(
                       ISNULL(d.HR0A,0)+ISNULL(d.HR1A,0)+ISNULL(d.HR2A,0)+ISNULL(d.HR3A,0)+ISNULL(d.HR4A,0)+
                       ISNULL(d.HR5A,0)+ISNULL(d.HR6A,0)+ISNULL(d.HR7A,0)+ISNULL(d.HR8A,0)+ISNULL(d.HR9A,0)+
                       ISNULL(d.HR10A,0)+ISNULL(d.HR11A,0)+ISNULL(d.HR12A,0)+ISNULL(d.HR13A,0)+ISNULL(d.HR14A,0)+
                       ISNULL(d.HR15A,0)+ISNULL(d.HR16A,0)+ISNULL(d.HR17A,0)+ISNULL(d.HR18A,0)+ISNULL(d.HR19A,0)+
                       ISNULL(d.HR20A,0)+ISNULL(d.HR21A,0)+ISNULL(d.HR22A,0)), 0)
  FROM bfldata.dbo.DailyCountCategoryTrf d WITH (NOLOCK)
  INNER JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = d.ShopName AND ds.SIMCountry = 'Ex2Locations'
 WHERE d.Warehouse = 'JAFZA' AND d.TrnDate BETWEEN @transferFrom AND @transferTo
 GROUP BY ds.Country, CAST(d.TrnDate AS DATE)

UNION ALL

SELECT ds.Country,
       TrfDate = CAST(d.TrnDate AS DATE),
       TransferQty = ISNULL(SUM(
                       ISNULL(d.HR0A,0)+ISNULL(d.HR1A,0)+ISNULL(d.HR2A,0)+ISNULL(d.HR3A,0)+ISNULL(d.HR4A,0)+
                       ISNULL(d.HR5A,0)+ISNULL(d.HR6A,0)+ISNULL(d.HR7A,0)+ISNULL(d.HR8A,0)+ISNULL(d.HR9A,0)+
                       ISNULL(d.HR10A,0)+ISNULL(d.HR11A,0)+ISNULL(d.HR12A,0)+ISNULL(d.HR13A,0)+ISNULL(d.HR14A,0)+
                       ISNULL(d.HR15A,0)+ISNULL(d.HR16A,0)+ISNULL(d.HR17A,0)+ISNULL(d.HR18A,0)+ISNULL(d.HR19A,0)+
                       ISNULL(d.HR20A,0)+ISNULL(d.HR21A,0)+ISNULL(d.HR22A,0)), 0)
  FROM bfldata.dbo.DailyCountCategoryTrf d WITH (NOLOCK)
  INNER JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = d.ShopName
 WHERE d.Warehouse = 'TECHNO' AND d.TrnDate BETWEEN @transferFrom AND @transferTo
   AND ds.Country IN ('UAE', 'OMAN')
 GROUP BY ds.Country, CAST(d.TrnDate AS DATE);

DROP TABLE #Scans, #BatchKind, #ItemDiv;";
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@fromDateOnly",        fromDateOnly));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@toDateExclusiveOnly", toDateExclusiveOnly));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@transferFrom",        fromDate.Date));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@transferTo",          toDateInclusive.Date));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@from",                fromInclusive));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@toExclusive",         toExclusive));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@country",             country));

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new ProductionCheckingRow(
                    ProductionDay: rdr.GetDateTime(0),
                    BatchNo:       rdr.IsDBNull(1) ? null : rdr.GetInt64(1),
                    Kind:          rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                    Division:      rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3),
                    TotalScanned:  rdr.IsDBNull(4) ? 0 : rdr.GetInt64(4),
                    StoreQty:      rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5)));
            }
            if (await rdr.NextResultAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    summary.Add(new ProductionCheckingSummaryRow(
                        ProductionDay: rdr.GetDateTime(0),
                        Kind:          rdr.IsDBNull(1) ? "Unknown" : rdr.GetString(1),
                        Division:      rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                        TotalScanned:  rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3),
                        StoreQty:      rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4),
                        UaeStoreQty:   rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5),
                        OmanStoreQty:  rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                        Ex2StoreQty:   rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7),
                        Ex2TotalScanned: rdr.IsDBNull(8) ? 0 : rdr.GetInt64(8)));
                }
            }
            if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
            {
                overallStoreQty = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            }
            if (await rdr.NextResultAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    ex2Shops.Add(new Ex2ShopRow(
                        Country:     rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        Date:        rdr.IsDBNull(1) ? DateTime.MinValue : rdr.GetDateTime(1),
                        TransferQty: rdr.IsDBNull(2) ? 0L : Convert.ToInt64(rdr.GetValue(2))));
                }
            }
        }

        // Transfer Qty — separate query, bfldata source. UAE keeps the Warehouse='TECHNO'
        // filter; other countries have no Warehouse/Country breakdown in this table, so
        // the filter is dropped and the raw (UAE-wide) total is returned instead.
        var transferQty = await GetTransferQtyAsync(conn, uaeOnly: true, fromDate, toDateInclusive, ct);

        return new ProductionCheckingResult(rows, summary, overallStoreQty, transferQty, ex2Shops);
    }

    // bfldata.dbo.DailyCountCategoryTrf has no Country column — only a UAE-warehouse
    // breakdown (JAFZA/TECHNO/YOTO). uaeOnly=true keeps the Warehouse='TECHNO' filter;
    // uaeOnly=false drops it, so the total returned is the UAE-wide figure, not a
    // per-country one.
    private async Task<long> GetTransferQtyAsync(
        SqlConnection conn, bool uaeOnly, DateTime fromDate, DateTime toDateInclusive, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT ISNULL(SUM(
                     ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                     ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                     ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                     ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                     ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)), 0) AS TransferQty
              FROM bfldata.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE {(uaeOnly ? "Warehouse = 'TECHNO' AND " : "")}TrnDate BETWEEN @from AND @to;";
        cmd.Parameters.Add(new SqlParameter("@from", fromDate.Date));
        cmd.Parameters.Add(new SqlParameter("@to",   toDateInclusive.Date));
        cmd.CommandTimeout = 60;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is not null && v is not DBNull ? Convert.ToInt64(v) : 0;
    }

    // TEMPORARY, see call site above — reads straight from BFLBAHRAIN's own
    // vTransferDetail (which does have TrfDate/Quantity) instead of the bfldata
    // Transfer table this report normally uses.
    private async Task<long> GetBahrainTransferQtyFromVTransferDetailAsync(
        SqlConnection conn, DateTime fromDate, DateTime toDateInclusive, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ISNULL(SUM(Quantity), 0)
              FROM [BFLBAHRAIN].dbo.vTransferDetail WITH (NOLOCK)
             WHERE TrfDate >= @from AND TrfDate < @toExclusive;";
        cmd.Parameters.Add(new SqlParameter("@from",        fromDate.Date));
        cmd.Parameters.Add(new SqlParameter("@toExclusive", toDateInclusive.Date.AddDays(1)));
        cmd.CommandTimeout = 60;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is not null && v is not DBNull ? Convert.ToInt64(v) : 0;
    }

    // ---------------- Non-UAE Production Checking (verbatim port of LPMSIM 1.14.268) ----------------
    // Phase 1: read raw scans from the country's dbo.amechecking via {Country}_DB_ConnectionString.
    // Phase 2: bulk-copy into a #Scans temp table on OnPremBackup, then run the same enrichment
    //          SQL the UAE path uses (LPMSIM_Batch country gate + Kind + Division + result sets).
    private async Task<ProductionCheckingResult> GetProductionCheckingViaConnStringAsync(
        string country, string connStr, DateTime fromDate, DateTime toDateInclusive, CancellationToken ct)
    {
        var fromInclusive       = fromDate.Date.AddHours(6);
        var toExclusive         = toDateInclusive.Date.AddDays(1).AddHours(6);
        var fromDateOnly        = fromDate.Date;
        var toDateExclusiveOnly = toDateInclusive.Date.AddDays(2);

        // Phase 1 — read raw scans from the country's server into a DataTable.
        // Transfer Qty is read from the same server (bfldata.dbo.DailyCountCategoryTrf
        // exists per-country, not just centrally), so it's fetched here too, via connStr.
        var scanTable = BuildScanDataTable();
        long transferQty;
        await using (var countryConn = new SqlConnection(WithConnectTimeout(connStr)))
        {
            await countryConn.OpenAsync(ct);
            await using var countryCmd = countryConn.CreateCommand();
            countryCmd.CommandText = CountryScanQuery;
            countryCmd.Parameters.Add(new SqlParameter("@fromDateOnly",        fromDateOnly));
            countryCmd.Parameters.Add(new SqlParameter("@toDateExclusiveOnly", toDateExclusiveOnly));
            countryCmd.Parameters.Add(new SqlParameter("@from",                fromInclusive));
            countryCmd.Parameters.Add(new SqlParameter("@toExclusive",         toExclusive));
            countryCmd.CommandTimeout = 120;

            await using var countryRdr = await countryCmd.ExecuteReaderAsync(ct);
            while (await countryRdr.ReadAsync(ct))
            {
                var row             = scanTable.NewRow();
                row["BatchNo"]      = countryRdr.IsDBNull(0) ? DBNull.Value : countryRdr.GetInt64(0);
                row["Itemcode"]     = countryRdr.GetString(1);
                row["ShopName"]     = countryRdr.GetString(2);
                row["Contno"]       = countryRdr.GetString(3);
                row["Result"]       = countryRdr.GetInt32(4);
                row["ProductionDay"] = countryRdr.GetDateTime(5);
                scanTable.Rows.Add(row);
            }
            await countryRdr.CloseAsync();

            transferQty = await GetTransferQtyAsync(countryConn, uaeOnly: false, fromDate, toDateInclusive, ct);
        }

        // Phase 2 — open OnPremBackup, create #Scans, bulk-copy in, run enrichment.
        var rows    = new List<ProductionCheckingRow>();
        var summary = new List<ProductionCheckingSummaryRow>();
        int overallStoreQty = 0;
        await using var conn = OpenOnPremBackup();

        // TEMPORARY: Bahrain's own bfldata.dbo.DailyCountCategoryTrf isn't populated yet,
        // so GetTransferQtyAsync above returns 0 for it. Until that's fixed, source
        // Bahrain's Transfer Qty from BFLBAHRAIN..vTransferDetail instead (reachable from
        // this OnPremBackup connection, same as the other per-country vTransferDetail
        // reads elsewhere in this codebase).
        if (string.Equals(country, "Bahrain", StringComparison.OrdinalIgnoreCase))
        {
            transferQty = await GetBahrainTransferQtyFromVTransferDetailAsync(conn, fromDate, toDateInclusive, ct);
        }

        if (scanTable.Rows.Count == 0)
            return new ProductionCheckingResult(rows, summary, 0, transferQty, new());

        await using (var createCmd = conn.CreateCommand())
        {
            createCmd.CommandText = CreateScansTempTable;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#Scans", BulkCopyTimeout = 60 })
        {
            bulk.ColumnMappings.Add("BatchNo",       "BatchNo");
            bulk.ColumnMappings.Add("Itemcode",      "Itemcode");
            bulk.ColumnMappings.Add("ShopName",      "ShopName");
            bulk.ColumnMappings.Add("Contno",        "Contno");
            bulk.ColumnMappings.Add("Result",        "Result");
            bulk.ColumnMappings.Add("ProductionDay", "ProductionDay");
            await bulk.WriteToServerAsync(scanTable, ct);
        }

        await using var enrichCmd = conn.CreateCommand();
        enrichCmd.CommandText = CountryEnrichmentQuery;
        enrichCmd.Parameters.Add(new SqlParameter("@country", country));
        enrichCmd.CommandTimeout = 300;

        await using (var rdr = await enrichCmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new ProductionCheckingRow(
                    ProductionDay: rdr.GetDateTime(0),
                    BatchNo:       rdr.IsDBNull(1) ? null : rdr.GetInt64(1),
                    Kind:          rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                    Division:      rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3),
                    TotalScanned:  rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4),
                    StoreQty:      rdr.IsDBNull(5) ? 0  : rdr.GetInt32(5)));
            }
            if (await rdr.NextResultAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    summary.Add(new ProductionCheckingSummaryRow(
                        ProductionDay: rdr.GetDateTime(0),
                        Kind:          rdr.IsDBNull(1) ? "Unknown" : rdr.GetString(1),
                        Division:      rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                        TotalScanned:  rdr.IsDBNull(3) ? 0L : rdr.GetInt64(3),
                        StoreQty:      rdr.IsDBNull(4) ? 0  : rdr.GetInt32(4),
                        UaeStoreQty:   rdr.IsDBNull(5) ? 0  : rdr.GetInt32(5),
                        OmanStoreQty:  rdr.IsDBNull(6) ? 0  : rdr.GetInt32(6),
                        Ex2StoreQty:   rdr.IsDBNull(7) ? 0  : rdr.GetInt32(7),
                        Ex2TotalScanned: rdr.IsDBNull(8) ? 0L : rdr.GetInt64(8)));
                }
            }
            if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
                overallStoreQty = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
        }

        return new ProductionCheckingResult(rows, summary, overallStoreQty, transferQty, new());
    }

    private static DataTable BuildScanDataTable()
    {
        var dt = new DataTable();
        var batchCol = dt.Columns.Add("BatchNo", typeof(long));
        batchCol.AllowDBNull = true;
        dt.Columns.Add("Itemcode",      typeof(string));
        dt.Columns.Add("ShopName",      typeof(string));
        dt.Columns.Add("Contno",        typeof(string));
        dt.Columns.Add("Result",        typeof(int));
        dt.Columns.Add("ProductionDay", typeof(DateTime));
        return dt;
    }

    private const string CreateScansTempTable = @"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#Scans') IS NOT NULL DROP TABLE #Scans;
CREATE TABLE #Scans (
    BatchNo       bigint        NULL,
    Itemcode      nvarchar(100) NOT NULL,
    ShopName      nvarchar(100) NOT NULL,
    Contno        nvarchar(100) NOT NULL,
    Result        int           NOT NULL,
    ProductionDay date          NOT NULL
);
CREATE CLUSTERED INDEX IX_Scans ON #Scans (BatchNo, Itemcode);";

    // No DB prefix — the connection string's Initial Catalog points to the right DB.
    private const string CountryScanQuery = @"
SELECT
    BatchNo = CASE
                  WHEN CHARINDEX('BP(', CmpName) > 0
                  THEN TRY_CAST(SUBSTRING(CmpName,
                                          CHARINDEX('BP(', CmpName) + 3,
                                          CHARINDEX(')',  CmpName, CHARINDEX('BP(', CmpName))
                                          - CHARINDEX('BP(', CmpName) - 3) AS bigint)
                  ELSE NULL
              END,
    Itemcode      = ISNULL(Itemcode, ''),
    ShopName      = ISNULL(ShopName, ''),
    Contno        = ISNULL(Contno,   ''),
    Result        = ISNULL(TRY_CAST(Result AS int), -1),
    ProductionDay = CAST(CASE
                             WHEN TRY_CAST(Time1 AS time) >= '06:00:00'
                                 THEN TrnDate
                             ELSE DATEADD(day, -1, TrnDate)
                         END AS date)
  FROM dbo.amechecking
 WHERE TrnDate >= @fromDateOnly
   AND TrnDate <  @toDateExclusiveOnly
   AND CAST(TrnDate AS datetime) + CAST(Time1 AS datetime) >= @from
   AND CAST(TrnDate AS datetime) + CAST(Time1 AS datetime) <  @toExclusive";

    private const string CountryEnrichmentQuery = @"
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#BatchKind') IS NOT NULL DROP TABLE #BatchKind;
IF OBJECT_ID('tempdb..#ItemDiv')   IS NOT NULL DROP TABLE #ItemDiv;

DELETE s
  FROM #Scans s
  INNER JOIN LPMSIM.dbo.LPMSIM_Batch b ON b.LPMBatchNo = s.BatchNo
 WHERE b.Country <> @country;

SELECT
    b.LPMBatchNo,
    Kind = CASE
               WHEN b.Sources LIKE '%Non-LPM%'
                AND REPLACE(b.Sources, 'Non-LPM', '') LIKE '%LPM%' THEN 'Mixed'
               WHEN b.Sources LIKE '%Non-LPM%' THEN 'Non-LPM'
               WHEN b.Sources LIKE '%LPM%'     THEN 'LPM'
               ELSE 'Unknown'
           END
  INTO #BatchKind
  FROM LPMSIM.dbo.LPMSIM_Batch b
 WHERE b.LPMBatchNo IN (SELECT DISTINCT BatchNo FROM #Scans WHERE BatchNo IS NOT NULL);

CREATE CLUSTERED INDEX IX_BatchKind ON #BatchKind (LPMBatchNo);

SELECT u.itemcode,
       Division = MIN(sm.Division)
  INTO #ItemDiv
  FROM (SELECT DISTINCT Itemcode FROM #Scans WHERE Itemcode <> '') si
  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = si.Itemcode
  INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
 GROUP BY u.itemcode;

CREATE CLUSTERED INDEX IX_ItemDiv ON #ItemDiv (itemcode);

SELECT
    s.ProductionDay,
    s.BatchNo,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END)
  FROM #Scans s
  LEFT JOIN #BatchKind bk  ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv   idv ON idv.itemcode  = s.Itemcode
 GROUP BY s.ProductionDay, s.BatchNo, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          ISNULL(s.BatchNo, -1) DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

SELECT
    s.ProductionDay,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END),
    UaeStoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'UAE'          THEN 1 ELSE 0 END),
    OmanStoreQty = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Oman'         THEN 1 ELSE 0 END),
    Ex2StoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Ex2Locations' THEN 1 ELSE 0 END),
    Ex2TotalScanned = COUNT_BIG(CASE WHEN ds.SIMCountry = 'Ex2Locations' THEN 1 END)
  FROM #Scans s
  LEFT JOIN #BatchKind         bk  ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv           idv ON idv.itemcode  = s.Itemcode
  LEFT JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = s.ShopName AND s.ShopName <> ''
 GROUP BY s.ProductionDay, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

SELECT OverallStoreQty = SUM(CASE WHEN Result IN (0, 13) THEN 1 ELSE 0 END)
  FROM #Scans;

DROP TABLE #Scans, #BatchKind, #ItemDiv;";

    // ===================== LPM WH Stock report (ported from LPMSIM) =====================
    /// <summary>Distinct PalletCategory values from bfldata.dbo.pallettype.</summary>
    public async Task<List<string>> GetPalletCategoriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT PalletCategory
              FROM bfldata.dbo.pallettype
             WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
             ORDER BY PalletCategory",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Ported from LPMSIM (WhHoStockService.GetLpmWhStockAsync). For each SIM
    /// country (or just the caller-selected ones), sums purchased LPM warehouse
    /// stock (LPMDt IS NOT NULL AND ShopEligible &lt;&gt; 'E') by (Country,
    /// Division, Season, LPMDt Year/Month) for the chosen PalletCategory
    /// (default ELIGIBLE).
    /// </summary>
    public async Task<List<LpmWhStockCell>> GetLpmWhStockAsync(
        string palletCategory, IEnumerable<string>? onlyCountries = null, CancellationToken ct = default)
    {
        var pc = string.IsNullOrWhiteSpace(palletCategory) ? "ELIGIBLE" : palletCategory.Trim();
        var only = onlyCountries?.Where(s => !string.IsNullOrWhiteSpace(s))
                                  .Select(s => s.Trim()).ToArray();
        // null (no argument at all) means "genuinely unrestricted" — an empty-but-non-null
        // list must NOT fall back to "show everything", since that's exactly what a
        // deny-by-default caller passes for a user with zero country grants.
        var hasCountryFilter = onlyCountries is not null;

        // Single read from the pre-aggregated snapshot. Snapshot is keyed on
        // (PalletCategory, Country, Division, Brand, Season, Year1, Month1) — the
        // report shape doesn't include Brand so we SUM(Qty) across brands.
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<LpmWhStockCell>(new CommandDefinition(@"
            SELECT Country,
                   Division,
                   Season,
                   Year1  AS [Year],
                   Month1 AS [Month],
                   SUM(CAST(ISNULL(Qty, 0) AS bigint)) AS Qty
              FROM dbo.LPM_WhStockSnapshot WITH (NOLOCK)
             WHERE PalletCategory = @pc
               AND (@noCountryFilter = 1 OR Country IN @countries)
             GROUP BY Country, Division, Season, Year1, Month1
            HAVING SUM(CAST(ISNULL(Qty, 0) AS bigint)) <> 0",
            new { pc,
                  noCountryFilter = hasCountryFilter ? 0 : 1,
                  countries = only ?? Array.Empty<string>() },
            commandTimeout: 60, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Non-LPM WH Stock report (ported from LPMSIM) =====================
    /// <summary>
    /// Ported from LPMSIM (WhHoStockService.GetNonLpmWhStockAsync). For every
    /// configured country (UAE, KSA, Kuwait, Qatar, Bahrain, MALAYSIA), sums
    /// Non-LPM eligible WH stock per Division × Season into one row per
    /// (Country, Division). Filter: LPMDt IS NULL, ShopEligible != 'E',
    /// PalletCategory = 'ELIGIBLE'. Season from whboxitems.Season.
    /// A misconfigured / unreadable country is skipped (not fatal).
    /// </summary>
    public async Task<List<NonLpmWhStockRow>> GetNonLpmWhStockAsync(CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();

        // 1) item → division map ONCE (global master tables)
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                IF OBJECT_ID('tempdb..#NlItemDiv') IS NOT NULL DROP TABLE #NlItemDiv;
                SELECT u.itemcode, Division = MIN(sm.Division)
                  INTO #NlItemDiv
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                 WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
                 GROUP BY u.itemcode;
                CREATE CLUSTERED INDEX IX_NlItemDiv ON #NlItemDiv (itemcode);";
            ddl.CommandTimeout = 120;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // 2) Fixed country set (per spec — excludes ECOM / virtual pseudo-countries).
        var countries = new List<string> { "UAE", "KSA", "Kuwait", "Qatar", "Bahrain", "MALAYSIA" };

        var rows = new List<NonLpmWhStockRow>();
        foreach (var country in countries)
        {
            string whSrc;
            try { whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct); }
            catch { continue; }   // no DataName / unreadable → skip

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT Division = ISNULL(id.Division, '(no division)'),
                           Summer = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) <> 'W'
                                             THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END),
                           Winter = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) =  'W'
                                             THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END)
                      FROM {whSrc} w
                      LEFT JOIN #NlItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.LPMDt IS NULL
                       AND ISNULL(w.ShopEligible,'') <> 'E'
                       AND UPPER(ISNULL(w.PalletCategory,'')) = 'ELIGIBLE'
                     GROUP BY ISNULL(id.Division, '(no division)')
                    HAVING SUM(CAST(ISNULL(w.Qty,0) AS bigint)) <> 0
                     ORDER BY Division;";
                cmd.CommandTimeout = 300;
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    rows.Add(new NonLpmWhStockRow(
                        Country:  country,
                        Division: rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        Summer:   rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1),
                        Winter:   rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2)));
                }
            }
            catch { /* one country's WH table missing/unreadable — skip it */ }
        }
        return rows;
    }

    public async Task<List<ItemSummaryReportRow>> ItemSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<ItemSummaryReportRow>(new CommandDefinition(BadBoxesPrefix + @"
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN d.QtyIssued          ELSE 0 END AS ExcessQty
                  FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                  INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
            ), agg AS (
                SELECT itemcode,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty
                  FROM base
                 GROUP BY itemcode
                HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            ), soh AS (
                SELECT itemcode, SUM(soh) AS HOStock
                  FROM racks.dbo.lpm_locstock WITH (NOLOCK)
                 WHERE itemcode IN (SELECT itemcode FROM agg)
                 GROUP BY itemcode
            )
            SELECT
                a.itemcode                AS ItemCode,
                im.description            AS ItemName,
                sub.Division              AS Division,
                sub.Department            AS Department,
                a.MissingQty              AS MissingQty,
                a.ExcessQty               AS ExcessQty,
                ISNULL(s.HOStock, 0)      AS HOStock
              FROM agg a
              LEFT JOIN hodata.dbo.itemmaster           im  WITH (NOLOCK) ON im.itemcode  = a.itemcode
              LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
              LEFT JOIN soh s                                              ON s.itemcode  = a.itemcode
             ORDER BY a.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
