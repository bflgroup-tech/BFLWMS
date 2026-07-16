using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Core;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Data service for the "OTS for PO Allocation" report.
///
/// Grain: one row per (Country, StoreID, DivCode) filtered by a picked
/// (Month, Year) and optional country filter. BFLGroup = no filter.
///
/// Each satellite lookup (SOH, Ex2SOH pair, WeekSales, StoreCount, WIP) is
/// run in its own try/catch so a single schema mismatch shows up as zeros
/// in that one column rather than blanking the whole grid. The list of
/// warnings is returned alongside the rows.
/// </summary>
public class OtsPoAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int CommandTimeoutSeconds = 300;

    public const string BflGroup = "BFLGroup";

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    public async Task<List<OtsMonthYearOption>> GetAvailableMonthYearsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<OtsMonthYearOption>(new CommandDefinition(@"
            SELECT DISTINCT Month1 AS Month, Year1 AS Year
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Month1 IS NOT NULL AND Year1 IS NOT NULL
             ORDER BY Year DESC, Month DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Country
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
               AND Country <> 'Ex2Locations'
             ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Distinct (DivCode, Division) pairs for the Division multi-select
    /// picker — sourced from LPM_EOM_Output filtered by picked Month/Year, joined
    /// to LPMSIM.dbo.Division for the human name.</summary>
    public async Task<List<(int DivCode, string Division)>> GetDivisionsAsync(
        int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(int DivCode, string Division)>(new CommandDefinition(@"
            SELECT DISTINCT e.DivCode, ISNULL(dv.Division, CAST(e.DivCode AS NVARCHAR(20))) AS Division
              FROM dbo.LPM_EOM_Output e WITH (NOLOCK)
              LEFT JOIN LPMSIM.dbo.Division dv WITH (NOLOCK) ON dv.DivCode = e.DivCode
             WHERE e.Month1 = @month AND e.Year1 = @year
               AND e.Country <> 'Ex2Locations'
             ORDER BY Division",
            new { month, year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Latest RunTS on the persisted table for a (Month, Year). Null =
    /// never generated. Used by the razor page to show "last generated" info.</summary>
    public async Task<DateTime?> GetLastGeneratedTsAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(@"
            SELECT MAX(RunTS) FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
             WHERE [Year] = @year AND [Month] = @month",
            new { month, year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Pre-Generate validation — checks each country present in the
    /// picked (Month, Year) has (a) an entry in WmsCountryOtsWeeks and (b) enough
    /// distinct weeks of data in lpm_salestgtwk_stores to cover the configured
    /// N-week window. Returns one issue string per country problem; empty list
    /// = all good. Non-blocking — the razor page can still force-generate.</summary>
    public async Task<List<string>> ValidateWeekCoverageAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = (await c.QueryAsync<(string Country, int? ConfiguredWeeks, int AvailableWeeks)>(new CommandDefinition(@"
            ;WITH baseCountries AS (
                SELECT DISTINCT Country FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE Month1 = @month AND Year1 = @year
                   AND Country <> 'Ex2Locations'
                   AND Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
            ),
            weekCounts AS (
                SELECT e.Country, COUNT(DISTINCT s.wk) AS Weeks
                  FROM dbo.LPM_EOM_Output e WITH (NOLOCK)
                  JOIN dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
                    ON s.StoreID = e.StoreID AND s.DivCode = e.DivCode
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND e.Country <> 'Ex2Locations'
                 GROUP BY e.Country
            )
            SELECT bc.Country,
                   w.Weeks               AS ConfiguredWeeks,
                   ISNULL(wc.Weeks, 0)   AS AvailableWeeks
              FROM baseCountries bc
              LEFT JOIN dbo.WmsCountryOtsWeeks w WITH (NOLOCK) ON w.SimCountry = bc.Country
              LEFT JOIN weekCounts wc ON wc.Country = bc.Country
             ORDER BY bc.Country",
            new { month, year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        var issues = new List<string>();
        foreach (var r in rows)
        {
            if (r.ConfiguredWeeks is null)
                issues.Add($"{r.Country}: not configured in WmsCountryOtsWeeks — falls back to 1 week of sales.");
            else if (r.AvailableWeeks < r.ConfiguredWeeks.Value)
                issues.Add($"{r.Country}: configured to include {r.ConfiguredWeeks.Value} week(s) of sales, but only {r.AvailableWeeks} distinct week(s) available in lpm_salestgtwk_stores.");
        }
        return issues;
    }

    /// <summary>Distinct OTSDate values already persisted for a (Month, Year).
    /// Used by the razor page's Rundate picker so operators can load prior days.</summary>
    public async Task<List<DateTime>> GetAvailableRunDatesAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<DateTime>(new CommandDefinition(@"
            SELECT DISTINCT OTSDate FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
             WHERE [Year] = @year AND [Month] = @month
               AND OTSDate IS NOT NULL
             ORDER BY OTSDate DESC",
            new { month, year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Load persisted rows for a (Month, Year, OTSDate), optionally
    /// filtered by country + a division subset. Country == null or BFLGroup
    /// means "all". divisions == null or empty means "all divisions".
    /// otsDate == null falls back to the latest available date for the (Month, Year).</summary>
    public async Task<List<OtsPoAllocationRow>> LoadPersistedAsync(
        int month, int year, DateTime? otsDate, string? country, IReadOnlyCollection<int>? divisions,
        CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(country) || string.Equals(country, BflGroup, StringComparison.OrdinalIgnoreCase)
            ? null : country;
        var eomLabel = new DateTime(year, month, 1).ToString("MMM-yyyy");
        var divClause = divisions is { Count: > 0 } ? "AND DivCode IN @divs" : "";
        var dateClause = otsDate.HasValue ? "AND OTSDate = @dt" : "";
        var sql = $@"
            SELECT Country, StoreID, StoreName, DivCode, Division, VolumeGroup, PriorityRank,
                   TgtEOM, SOHToday, WeeksToInclude, WeekSales, InTransit, Ex2DcSoh,
                   CountingWIP, OtsQtyToday, OtsPercentToday,
                   ISNULL(PrevMonthEOM, 0) AS PrevMonthEOM,
                   ISNULL(WkReduction,  0) AS WkReduction,
                   ISNULL(CurrentEOW,   TgtEOM) AS CurrentEOW
              FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
             WHERE [Year] = @year AND [Month] = @month
               AND (@ct IS NULL OR Country = @ct)
               {dateClause}
               {divClause}
             ORDER BY Country, StoreID, DivCode";
        await using var c = OpenWms();
        var rows = await c.QueryAsync<PersistedRow>(new CommandDefinition(
            sql, new { month, year, ct = filter, divs = divisions?.ToArray(), dt = otsDate?.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Select(r => new OtsPoAllocationRow(
            Country: r.Country, StoreID: r.StoreID, StoreName: r.StoreName,
            DivCode: r.DivCode, Division: r.Division, VolumeGroup: r.VolumeGroup,
            PriorityRank: r.PriorityRank, EOMMonth: eomLabel,
            TgtEOM: r.TgtEOM, SOHToday: r.SOHToday, WeeksToInclude: r.WeeksToInclude,
            WeekSales: r.WeekSales, InTransit: r.InTransit, Ex2DcSoh: r.Ex2DcSoh,
            CountingWIP: r.CountingWIP, OtsQtyToday: r.OtsQtyToday,
            OtsPercentToday: (double)r.OtsPercentToday,
            PrevMonthEOM: r.PrevMonthEOM,
            WkReduction: r.WkReduction,
            CurrentEOW: r.CurrentEOW)).ToList();
    }

    /// <summary>Runs the full compute for (Month, Year) across all countries and
    /// persists to dbo.WmsOtsPoAllocationRun stamped with OTSDate=today (GST).
    /// Any prior rows for the SAME OTSDate are DELETEd first so re-running
    /// on the same day replaces itself but keeps prior days intact. Callers
    /// should follow with LoadPersistedAsync. Only valid when Country=BFLGroup;
    /// the razor page enforces that.</summary>
    public async Task<(int RowsPersisted, List<string> Warnings)> GenerateAndPersistAsync(
        int month, int year, CancellationToken ct = default)
    {
        var (rows, warnings) = await GenerateAsync(month, year, country: null, ct);
        if (rows.Count == 0) return (0, warnings);

        var nowGst   = DateTime.UtcNow.AddHours(4);
        var otsDate  = nowGst.Date;   // today (GST), no time

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                DELETE FROM dbo.WmsOtsPoAllocationRun
                 WHERE [Year] = @year AND [Month] = @month AND OTSDate = @dt",
                new { month, year, dt = otsDate }, transaction: tx,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var dt = new System.Data.DataTable();
            dt.Columns.Add("RunTS",           typeof(DateTime));
            dt.Columns.Add("RunBy",           typeof(string));
            dt.Columns.Add("Month",           typeof(int));
            dt.Columns.Add("Year",            typeof(int));
            dt.Columns.Add("OTSDate",         typeof(DateTime));
            dt.Columns.Add("Country",         typeof(string));
            dt.Columns.Add("StoreID",         typeof(string));
            dt.Columns.Add("StoreName",       typeof(string));
            dt.Columns.Add("DivCode",         typeof(int));
            dt.Columns.Add("Division",        typeof(string));
            dt.Columns.Add("VolumeGroup",     typeof(string));
            dt.Columns.Add("PriorityRank",    typeof(int));
            dt.Columns.Add("TgtEOM",          typeof(int));
            dt.Columns.Add("SOHToday",        typeof(int));
            dt.Columns.Add("WeeksToInclude",  typeof(int));
            dt.Columns.Add("WeekSales",       typeof(int));
            dt.Columns.Add("InTransit",       typeof(int));
            dt.Columns.Add("Ex2DcSoh",        typeof(int));
            dt.Columns.Add("CountingWIP",     typeof(int));
            dt.Columns.Add("OtsQtyToday",     typeof(int));
            dt.Columns.Add("OtsPercentToday", typeof(decimal));
            dt.Columns.Add("PrevMonthEOM",    typeof(int));
            dt.Columns.Add("WkReduction",     typeof(decimal));
            dt.Columns.Add("CurrentEOW",      typeof(int));

            var who = user.Name ?? "";
            foreach (var r in rows)
            {
                dt.Rows.Add(
                    nowGst, who, month, year, otsDate,
                    r.Country, r.StoreID, (object?)r.StoreName ?? DBNull.Value,
                    r.DivCode, (object?)r.Division ?? DBNull.Value,
                    (object?)r.VolumeGroup ?? DBNull.Value,
                    (object?)r.PriorityRank ?? DBNull.Value,
                    r.TgtEOM, r.SOHToday, r.WeeksToInclude, r.WeekSales,
                    r.InTransit, r.Ex2DcSoh, r.CountingWIP, r.OtsQtyToday,
                    (decimal)r.OtsPercentToday,
                    r.PrevMonthEOM, r.WkReduction, r.CurrentEOW);
            }

            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.WmsOtsPoAllocationRun",
                BatchSize = 1000,
                BulkCopyTimeout = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
        return (rows.Count, warnings);
    }

    /// <summary>Main report. country == null OR "BFLGroup" means no country filter.
    /// Kept for the direct/live path — persistence is done by GenerateAndPersistAsync.</summary>
    public async Task<(List<OtsPoAllocationRow> Rows, List<string> Warnings)> GenerateAsync(
        int month, int year, string? country, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var filter = string.IsNullOrWhiteSpace(country) || string.Equals(country, BflGroup, StringComparison.OrdinalIgnoreCase)
            ? null : country;

        // 1) Base rows (reliable — well-known columns): LPM_EOM_Output +
        //    Divisions (name) + DataSettings (store name) + WmsCountryOtsWeeks.
        List<BaseRow> baseRows;
        await using (var c = OpenOnPremBackup())
        {
            // Perf: replace the per-row OUTER APPLY on DataSettings with a
            // single-pass CTE dedup (one ROW_NUMBER per StoreID) that a plain
            // LEFT JOIN can then use. Cuts thousands of mini-lookups down to
            // one aggregate pass.
            baseRows = (await c.QueryAsync<BaseRow>(new CommandDefinition(@"
                ;WITH storeNames AS (
                    SELECT StoreID, PBFullname,
                           rn = ROW_NUMBER() OVER (PARTITION BY StoreID ORDER BY PBFullname)
                      FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                     WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
                       AND PBFullname IS NOT NULL AND LTRIM(RTRIM(PBFullname)) <> ''
                )
                SELECT
                    e.Country,
                    e.StoreID,
                    sn.PBFullname AS StoreName,
                    e.DivCode,
                    dv.Division   AS Division,
                    e.VolumeGroup,
                    e.PriorityRank,
                    e.TargetEOM   AS TgtEOM,
                    ISNULL(w.Weeks, 1) AS WeeksToInclude,
                    ISNULL((
                        SELECT SUM(prev.TargetEOM)
                          FROM dbo.LPM_EOM_Output prev WITH (NOLOCK)
                         WHERE prev.StoreID = e.StoreID
                           AND prev.DivCode = e.DivCode
                           AND prev.Month1  = @prevMonth
                           AND prev.Year1   = @prevYear
                    ), 0) AS PrevMonthEOM
                  FROM dbo.LPM_EOM_Output e WITH (NOLOCK)
                  LEFT JOIN LPMSIM.dbo.Division dv WITH (NOLOCK) ON dv.DivCode = e.DivCode
                  LEFT JOIN storeNames sn ON sn.StoreID = e.StoreID AND sn.rn = 1
                  LEFT JOIN dbo.WmsCountryOtsWeeks w WITH (NOLOCK) ON w.SimCountry = e.Country
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND e.Country <> 'Ex2Locations'
                   AND (@ct IS NULL OR e.Country = @ct)
                 ORDER BY e.Country, e.StoreID, e.DivCode",
                new { month, year,
                      prevMonth = month == 1 ? 12 : month - 1,
                      prevYear  = month == 1 ? year - 1 : year,
                      ct = filter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }

        if (baseRows.Count == 0) return (new(), warnings);

        // Satellites 2-6 run concurrently — each opens its own connection so
        // there's no shared state. Warnings.Add is guarded by a lock inside
        // SafeAsync.
        var weeksByCountry = baseRows.GroupBy(b => b.Country).ToDictionary(g => g.Key, g => g.First().WeeksToInclude);

        // 2) SOH Today per (StoreID, DivCode) from Racks.dbo.LPM_Locstock.
        //    StoreID keys are upper-cased so the ECom special-case below can
        //    look up "ONLINE" + "ONLINEKSA" without worrying about source casing.
        var sohTask = SafeAsync(warnings, "SOH Today (Racks.dbo.LPM_Locstock)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(string StoreID, int DivCode, int SOH)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, SUM(ISNULL(SOH, 0)) AS SOH
                  FROM Racks.dbo.LPM_Locstock WITH (NOLOCK)
                 GROUP BY StoreID, DivCode",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(
                r => (r.StoreID.Trim().ToUpperInvariant(), r.DivCode),
                r => r.SOH);
        }, () => new Dictionary<(string, int), int>());

        // 3) Ex2 SOH pair per (Country, DivCode) via LPM_Ex2LocationConfig + vupc_subclass.
        //    Country casing differs across sources — LPM_Ex2LocationConfig has
        //    'BAHRAIN', 'KUWAIT', 'QATAR', 'Malaysia', 'KSA'; LPM_EOM_Output has
        //    'Bahrain', 'Kuwait', 'Qatar', 'MALAYSIA', 'KSA'. Only KSA matched
        //    by luck. Upper-case both sides here + in the merge lookup below.
        var ex2Task = SafeAsync(warnings, "InTransit/Ex2 DC SOH (LPM_Ex2ItemSOH + LPM_Ex2LocationConfig)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(string Country, int DivCode, int InTransitTotal, int Ex2DcTotal)>(new CommandDefinition(@"
                SELECT UPPER(LTRIM(RTRIM(cfg.Country))) AS Country,
                       v.DivID                          AS DivCode,
                       SUM(ISNULL(sohs.Ex2SOH, 0) + ISNULL(sohs.BoxSOH, 0)) AS InTransitTotal,
                       SUM(ISNULL(sohs.R1WHSOH, 0))                         AS Ex2DcTotal
                  FROM dbo.LPM_Ex2ItemSOH sohs WITH (NOLOCK)
                  JOIN dbo.LPM_Ex2LocationConfig cfg WITH (NOLOCK)
                    ON cfg.Ex2StoreID = sohs.Ex2StoreID
                  JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                    ON v.itemcode = sohs.Itemcode
                 WHERE v.DivID IS NOT NULL AND cfg.Country IS NOT NULL
                 GROUP BY UPPER(LTRIM(RTRIM(cfg.Country))), v.DivID",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(r => (r.Country, r.DivCode), r => (r.InTransitTotal, r.Ex2DcTotal));
        }, () => new Dictionary<(string, int), (int, int)>());

        // 4) Week Sales per (StoreID, DivCode) summed over next N weeks from currentWk.
        var weekSalesTask = SafeAsync(warnings, "Week Sales (lpm_salestgtwk_stores)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(string StoreID, int DivCode, string Country, int Wk, int Sales)>(new CommandDefinition(@"
                SELECT s.StoreID, s.DivCode, e.Country, s.wk AS Wk, ISNULL(s.SalesTgtWk, 0) AS Sales
                  FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
                  JOIN dbo.LPM_EOM_Output e WITH (NOLOCK)
                    ON e.StoreID = s.StoreID AND e.DivCode = s.DivCode
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND (@ct IS NULL OR e.Country = @ct)",
                new { month, year, ct = filter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var minWk = rows.Any() ? rows.Min(r => r.Wk) : 0;
            return rows
                .GroupBy(r => (r.StoreID, r.DivCode))
                .ToDictionary(g => g.Key, g =>
                {
                    var cty = g.First().Country;
                    var n = weeksByCountry.TryGetValue(cty, out var w) ? w : 1;
                    return g.Where(x => x.Wk >= minWk && x.Wk < minWk + n).Sum(x => x.Sales);
                });
        }, () => new Dictionary<(string, int), int>());

        // 5) Store count per country from LPM_EOM_Output.
        var storeCountTask = SafeAsync(warnings, "Store count", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(string Country, int Cnt)>(new CommandDefinition(@"
                SELECT Country, COUNT(DISTINCT StoreID) AS Cnt
                  FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE Month1 = @month AND Year1 = @year
                 GROUP BY Country",
                new { month, year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(r => r.Country, r => r.Cnt);
        }, () => new Dictionary<string, int>());

        // 6) Counting WIP on Azure — SUM(AllocatedQty) per (Country, StoreID, DivCode)
        //    for contnos with approved LPMSIM header but no WmsBuildingCompletion.
        //    Perf: fetch the approved-contno list first, then let Azure do the
        //    filter + aggregate in one round-trip (no full-table pull to C#).
        var wipTask = SafeAsync(warnings, "Counting WIP (Azure)", async () =>
        {
            HashSet<string> approvedContnos;
            await using (var opb = OpenOnPremBackup())
            {
                approvedContnos = (await opb.QueryAsync<string>(new CommandDefinition(@"
                    SELECT DISTINCT ContNo
                      FROM dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
                     WHERE ApprovedDt IS NOT NULL",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            if (approvedContnos.Count == 0) return new Dictionary<(string, string, int), int>();

            await using var w = OpenWms();
            var contnos = approvedContnos.ToArray();
            var rows = await w.QueryAsync<(string Country, string StoreID, int DivCode, int Qty)>(
                new CommandDefinition(@"
                    SELECT a.Country, a.StoreID,
                           DivCode = ISNULL(a.DivCode, 0),
                           Qty     = SUM(ISNULL(a.AllocatedQty, 0))
                      FROM dbo.WMS_ContAllocationData a WITH (NOLOCK)
                     WHERE a.StoreID IS NOT NULL
                       AND a.ContNo IN @contnos
                       AND NOT EXISTS (
                           SELECT 1 FROM dbo.WmsBuildingCompletion b WITH (NOLOCK)
                            WHERE b.ContNo = a.ContNo
                       )
                     GROUP BY a.Country, a.StoreID, ISNULL(a.DivCode, 0)",
                    new { contnos },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(r => (r.Country ?? "", r.StoreID, r.DivCode), r => r.Qty);
        }, () => new Dictionary<(string, string, int), int>());

        await Task.WhenAll(sohTask, ex2Task, weekSalesTask, storeCountTask, wipTask);
        var sohByKey            = sohTask.Result;
        var ex2ByKey            = ex2Task.Result;
        var weekSalesByKey      = weekSalesTask.Result;
        var storeCountByCountry = storeCountTask.Result;
        var wipByKey            = wipTask.Result;

        // 7) Merge + compute OTS Qty / OTS %.
        //
        // OTS uses CurrentEOW (interpolated end-of-week target) in place of
        // TgtEOM per current spec:
        //   WkReduction = (PrevMonthEOM - TgtEOM) / weeksInMonth
        //   CurrentEOW  = PrevMonthEOM - (WkReduction * weeksElapsedSoFar)
        //   OTS Qty     = CurrentEOW + WeekSales - SOH - InTransit - Ex2DC - CountingWIP
        //   OTS %       = OTS Qty / CurrentEOW * 100  (0 when CurrentEOW <= 0)
        // Stores with PrevMonthEOM = 0 fall back to CurrentEOW = TgtEOM so the
        // formula reduces to today's behaviour for stores without history.
        //
        // weeksInMonth  = distinct wk values in lpm_salestgtwk_stores for stores
        //                 active in this month's LPM_EOM_Output. Falls back to
        //                 ceiling(daysInMonth / 7) if the salestgtwk source is
        //                 empty or unavailable (SafeAsync catches errors).
        // weeksElapsed  = runDay.Day / 7 (integer division: only fully completed
        //                 weeks count). Unchanged from prior spec.
        var eomLabel   = new DateTime(year, month, 1).ToString("MMM-yyyy");
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var weeksInMonth = await SafeAsync(warnings, "weeksInMonth (lpm_salestgtwk_stores)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var v = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT COUNT(DISTINCT s.wk)
                  FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
                  JOIN dbo.LPM_EOM_Output e WITH (NOLOCK)
                    ON e.StoreID = s.StoreID AND e.DivCode = s.DivCode
                 WHERE e.Month1 = @month AND e.Year1 = @year",
                new { month, year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return v ?? 0;
        }, () => 0);
        if (weeksInMonth <= 0)
            weeksInMonth = (int)Math.Ceiling(daysInMonth / 7.0);

        var runDay       = DateTime.UtcNow.AddHours(4);
        var isCurrentGst = runDay.Year == year && runDay.Month == month;
        // Weeks fully completed as of the run day — integer division rounds
        // DOWN (day 15 → 2 weeks, not 3; day 21 → 3; day 28 → 4).
        var weeksElapsed = isCurrentGst
            ? Math.Min(weeksInMonth, runDay.Day / 7)
            : weeksInMonth;    // for past/future months, treat as fully-elapsed
        var results = new List<OtsPoAllocationRow>(baseRows.Count);
        foreach (var r in baseRows)
        {
            // SOH lookup — ECOM special-case sums SOH across the Online (UAE)
            // AND OnlineKSA stores, since a single ECOM base row represents the
            // combined online channel.
            int soh;
            if (string.Equals(r.Country, "ECOM", StringComparison.OrdinalIgnoreCase))
            {
                var sUae = sohByKey.TryGetValue(("ONLINE",    r.DivCode), out var v1) ? v1 : 0;
                var sKsa = sohByKey.TryGetValue(("ONLINEKSA", r.DivCode), out var v2) ? v2 : 0;
                soh = sUae + sKsa;
            }
            else
            {
                soh = sohByKey.TryGetValue((r.StoreID.Trim().ToUpperInvariant(), r.DivCode), out var s) ? s : 0;
            }
            var ws     = weekSalesByKey.TryGetValue((r.StoreID, r.DivCode), out var w) ? w : 0;
            var stores = storeCountByCountry.TryGetValue(r.Country, out var sc) ? sc : 0;
            int inTransit = 0, ex2dc = 0;
            var isUAE = string.Equals(r.Country, "UAE", StringComparison.OrdinalIgnoreCase);
            var cKey  = (r.Country ?? "").Trim().ToUpperInvariant();
            if (!isUAE
                && ex2ByKey.TryGetValue((cKey, r.DivCode), out var e)
                && stores > 0)
            {
                var (inTransitTotal, ex2DcTotal) = e;
                inTransit = inTransitTotal / stores;
                ex2dc     = ex2DcTotal    / stores;
            }
            var wip = wipByKey.TryGetValue((r.Country, r.StoreID, r.DivCode), out var v) ? v : 0;

            decimal wkReduction;
            int currentEOW;
            if (r.PrevMonthEOM > 0 && weeksInMonth > 0)
            {
                wkReduction = (decimal)(r.PrevMonthEOM - r.TgtEOM) / weeksInMonth;
                currentEOW  = (int)Math.Round(r.PrevMonthEOM - wkReduction * weeksElapsed);
            }
            else
            {
                wkReduction = 0m;
                currentEOW  = r.TgtEOM;   // fall back to today's behaviour
            }

            var otsQty = currentEOW + ws - soh - inTransit - ex2dc - wip;
            var otsPct = currentEOW > 0 ? (double)otsQty / currentEOW * 100.0 : 0.0;
            results.Add(new OtsPoAllocationRow(
                Country:         r.Country,
                StoreID:         r.StoreID,
                StoreName:       r.StoreName,
                DivCode:         r.DivCode,
                Division:        r.Division,
                VolumeGroup:     r.VolumeGroup,
                PriorityRank:    r.PriorityRank,
                EOMMonth:        eomLabel,
                TgtEOM:          r.TgtEOM,
                SOHToday:        soh,
                WeeksToInclude:  r.WeeksToInclude,
                WeekSales:       ws,
                InTransit:       inTransit,
                Ex2DcSoh:        ex2dc,
                CountingWIP:     wip,
                OtsQtyToday:     otsQty,
                OtsPercentToday: otsPct,
                PrevMonthEOM:    r.PrevMonthEOM,
                WkReduction:     wkReduction,
                CurrentEOW:      currentEOW));
        }
        return (results, warnings);
    }

    private static async Task<T> SafeAsync<T>(List<string> warnings, string label, Func<Task<T>> body, Func<T> fallback)
    {
        try { return await body(); }
        catch (Exception ex)
        {
            // warnings can be appended from multiple parallel satellite tasks.
            lock (warnings) { warnings.Add($"{label} unavailable: {ex.Message}"); }
            return fallback();
        }
    }

    private sealed class BaseRow
    {
        public string   Country        { get; set; } = "";
        public string   StoreID        { get; set; } = "";
        public string?  StoreName      { get; set; }
        public int      DivCode        { get; set; }
        public string?  Division       { get; set; }
        public string?  VolumeGroup    { get; set; }
        public int?     PriorityRank   { get; set; }
        public int      TgtEOM         { get; set; }
        public int      WeeksToInclude { get; set; }
        public int      PrevMonthEOM   { get; set; }
    }

    private sealed class PersistedRow
    {
        public string   Country         { get; set; } = "";
        public string   StoreID         { get; set; } = "";
        public string?  StoreName       { get; set; }
        public int      DivCode         { get; set; }
        public string?  Division        { get; set; }
        public string?  VolumeGroup     { get; set; }
        public int?     PriorityRank    { get; set; }
        public int      TgtEOM          { get; set; }
        public int      SOHToday        { get; set; }
        public int      WeeksToInclude  { get; set; }
        public int      WeekSales       { get; set; }
        public int      InTransit       { get; set; }
        public int      Ex2DcSoh        { get; set; }
        public int      CountingWIP     { get; set; }
        public int      OtsQtyToday     { get; set; }
        public decimal  OtsPercentToday { get; set; }
        public int      PrevMonthEOM    { get; set; }
        public decimal  WkReduction     { get; set; }
        public int      CurrentEOW      { get; set; }
    }
}
