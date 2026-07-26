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
        await using var c = OpenOnPremBackup();
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

    /// <summary>Current fiscal week anchor — the same wk the Week Sales sum
    /// starts at inside GenerateAsync (min wk in lpm_salestgtwk_stores across
    /// stores active in the picked Month/Year). Nullable when nothing matches.
    /// Used by the OTS page for the Week Sales tooltip so the wk range shown
    /// matches what the algorithm actually summed. Falls back to
    /// LPM_OTS_Output.wk when lpm_salestgtwk_stores is empty for the month.</summary>
    public async Task<int?> GetCurrentOtsWkAsync(int? month = null, int? year = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Preferred source: same table WeekSales reads from.
        var wk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT MIN(s.wk)
              FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
              JOIN dbo.LPM_EOM_Output e WITH (NOLOCK)
                ON e.StoreID = s.StoreID AND e.DivCode = s.DivCode
             WHERE (@m IS NULL OR e.Month1 = @m)
               AND (@y IS NULL OR e.Year1  = @y)
               AND s.wk IS NOT NULL",
            new { m = month, y = year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (wk is int v && v > 0) return v;

        // Fallback: LPM_OTS_Output (may be sparsely populated).
        return await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT TOP 1 wk FROM dbo.LPM_OTS_Output WITH (NOLOCK)
             WHERE wk IS NOT NULL
             ORDER BY OTSDate DESC, wk DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Distinct OTSDate values already persisted for a (Month, Year).
    /// Used by the razor page's Rundate picker so operators can load prior days.</summary>
    public async Task<List<DateTime>> GetAvailableRunDatesAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
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
        await using var c = OpenOnPremBackup();
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
        // Precondition: Volume Group must have been (re-)generated today (GST).
        // Enforces the daily refresh chain so OTS numbers don't ride on a stale
        // StoreDivGrade snapshot.
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        await using (var chk = OpenOnPremBackup())
        {
            var vgToday = await chk.ExecuteScalarAsync<int>(new CommandDefinition(@"
                SELECT COUNT(1) FROM dbo.StoreDivGrade WITH (NOLOCK)
                 WHERE CAST(GeneratedTS AS DATE) = @dt",
                new { dt = todayGst }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (vgToday == 0)
                throw new InvalidOperationException(
                    $"Volume Group has not been generated today ({todayGst:dd/MM/yyyy} GST). " +
                    "Click 'Generate Volume Group' first, then re-run 'Generate' on OTS for PO Allocation.");
        }

        var (rows, warnings) = await GenerateAsync(month, year, country: null, ct);
        if (rows.Count == 0) return (0, warnings);

        var nowGst   = DateTime.UtcNow.AddHours(4);
        var otsDate  = nowGst.Date;   // today (GST), no time

        await using var c = OpenOnPremBackup();
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
                ,
                sdgLatest AS (
                    -- Latest StoreDivGrade row per (StoreID, DivCode) at or before the
                    -- picked Month/Year. Grade here supersedes LPM_EOM_Output.VolumeGroup
                    -- (which the schema keeps for legacy consumers).
                    SELECT sdg.StoreID, sdg.DivCode, sdg.Grade,
                           ROW_NUMBER() OVER (PARTITION BY sdg.StoreID, sdg.DivCode
                                              ORDER BY sdg.Year1 DESC, sdg.Month1 DESC) AS rn
                      FROM LPMSIM.dbo.StoreDivGrade sdg WITH (NOLOCK)
                     WHERE (sdg.Year1 * 100 + sdg.Month1) <= (@year * 100 + @month)
                )
                SELECT
                    e.Country,
                    e.StoreID,
                    sn.PBFullname AS StoreName,
                    e.DivCode,
                    dv.Division   AS Division,
                    sdg.Grade AS VolumeGroup,   -- source of truth: StoreDivGrade only; blank when never Generated
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
                  LEFT JOIN sdgLatest sdg ON sdg.StoreID = e.StoreID AND sdg.DivCode = e.DivCode AND sdg.rn = 1
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
        //    lpm_salestgtwk_stores gives per-store weekly sales; MFP OUTBOUND holds
        //    the authoritative country-division total. We use lpm's per-store share
        //    but scale to MFP's total so country-x-div sums exactly match MFP.
        //
        //      final ws (per store) = existing ws * (mfpTotal / countryDivExisting)
        //
        //    MFP grain is (territory, division, week, year) - no StoreID - so the
        //    scaling preserves the per-store shape and makes MFP the source of
        //    truth at country-x-div level. LPM_MfpTerritoryMap maps MFP's territory
        //    code ('ae', 'sa', ...) to the SIMCountry we already use.
        var weekSalesTask = SafeAsync(warnings, "Week Sales (lpm_salestgtwk_stores x BFL_MFP_OUTBOUND_T1)", async () =>
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
            var maxWkPerCountry = rows.Any()
                ? rows.GroupBy(r => r.Country).ToDictionary(
                    g => g.Key,
                    g => minWk + (weeksByCountry.TryGetValue(g.Key, out var w) ? w : 1) - 1)
                : new Dictionary<string, int>();

            // Existing per-store WeekSales (using lpm_salestgtwk_stores).
            var existing = rows
                .GroupBy(r => (r.StoreID, r.DivCode))
                .ToDictionary(g => g.Key, g =>
                {
                    var cty = g.First().Country;
                    var n = weeksByCountry.TryGetValue(cty, out var w) ? w : 1;
                    return g.Where(x => x.Wk >= minWk && x.Wk < minWk + n).Sum(x => x.Sales);
                });

            if (minWk == 0 || !rows.Any()) return existing;

            // Pull MFP totals for the wk range and rescale existing per-store values.
            var globalMaxWk = maxWkPerCountry.Values.DefaultIfEmpty(minWk).Max();
            var mfpRows = await c.QueryAsync<(string Country, int DivCode, int Wk, decimal Planned)>(new CommandDefinition(@"
                SELECT tm.SIMCountry AS Country, m.division AS DivCode, m.week AS Wk,
                       SUM(ISNULL(m.planned_sls, 0)) AS Planned
                  FROM dbo.BFL_MFP_OUTBOUND_T1 m WITH (NOLOCK)
                  JOIN dbo.LPM_MfpTerritoryMap tm WITH (NOLOCK)
                       ON tm.Territory = m.territory AND tm.IsActive = 1
                 WHERE m.[year] = @year
                   AND m.week BETWEEN @minWk AND @maxWk
                 GROUP BY tm.SIMCountry, m.division, m.week",
                new { year, minWk, maxWk = globalMaxWk },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            // Sum MFP planned_sls for the wk range per country (respecting per-country N).
            var mfpTotals = mfpRows
                .Where(r => maxWkPerCountry.TryGetValue(r.Country, out var mx) ? r.Wk <= mx : true)
                .GroupBy(r => (r.Country, r.DivCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Planned));

            // Country-x-DivCode existing sums, drive the ratio for each store.
            var existingByCountryDiv = new Dictionary<(string, int), int>();
            foreach (var kv in existing)
            {
                var (storeId, div) = kv.Key;
                var cty = rows.First(r => r.StoreID == storeId && r.DivCode == div).Country;
                var key = (cty, div);
                existingByCountryDiv[key] = existingByCountryDiv.GetValueOrDefault(key, 0) + kv.Value;
            }

            // Rescale so country-x-div sums match MFP totals.
            var scaled = new Dictionary<(string StoreID, int DivCode), int>(existing.Count);
            foreach (var kv in existing)
            {
                var (storeId, div) = kv.Key;
                var cty = rows.First(r => r.StoreID == storeId && r.DivCode == div).Country;
                if (mfpTotals.TryGetValue((cty, div), out var mfp) && mfp > 0)
                {
                    var existingCd = existingByCountryDiv.GetValueOrDefault((cty, div), 0);
                    if (existingCd > 0)
                    {
                        var scale = (double)mfp / existingCd;
                        scaled[kv.Key] = (int)Math.Round(kv.Value * scale);
                    }
                    else
                    {
                        // No lpm share to distribute against -> keep existing (usually 0).
                        scaled[kv.Key] = kv.Value;
                    }
                }
                else
                {
                    // MFP has no data for this country-x-div -> fall back to existing.
                    scaled[kv.Key] = kv.Value;
                }
            }
            return scaled;
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

    // ==============================================================
    // "Generate Volume Group" — populate dbo.StoreDivGrade per (Month, Year)
    //
    // Rule (per operator spec):
    //   * SalesAmt   = LPM_EOM_Output.targetsales for the month/year row.
    //   * AvgSalesAmt = AVG(SalesAmt) per DivCode across all non-ECOM stores.
    //   * AvgSalesPct = SalesAmt / AvgSalesAmt * 100  (null for ECOM rows).
    //   * Grade:
    //       Country = 'ECOM'                    -> 'Z' (fixed)
    //       Non-ECOM, top-K by AvgSalesPct DESC -> 'A' where K = max(2, count(pct > 300))
    //       Rest                                -> LPM_VolumeGroupRange band lookup
    //                                              (IsSpecial = 0 rows only)
    // Idempotent: DELETE existing rows for (Month, Year) then bulk insert.
    // ==============================================================
    public async Task<int> GenerateStoreDivGradesAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();

        // 1a) Base set — one row per (Country, StoreID, DivCode) to grade.
        //     Country comes from LPM_EOM_Output; the sales amount used for
        //     grading is now sourced from the weighted weekly rollup below.
        var baseRows = (await c.QueryAsync<(string Country, string StoreID, int DivCode, decimal? SalesAmt)>(new CommandDefinition(@"
            SELECT Country, StoreID, DivCode, CAST(NULL AS DECIMAL(18,2)) AS SalesAmt
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Month1 = @m AND Year1 = @y
               AND Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
               AND Country <> 'Ex2Locations'",
            new { m = month, y = year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        if (baseRows.Count == 0) return 0;

        // 1b) Anchor = latest fiscal week from LPM_OTS_Output. wk is per-year
        //     (resets each year) so we read (Year, wk) from the row with the
        //     most recent OTSDate. Weekly rollup rows with (Year1, Week) at
        //     or before that anchor are eligible.
        var anchor = (await c.QueryAsync<(int Year, int Week)>(new CommandDefinition(@"
            SELECT TOP 1 YEAR(OTSDate) AS [Year], wk AS [Week]
              FROM dbo.LPM_OTS_Output WITH (NOLOCK)
             ORDER BY OTSDate DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).FirstOrDefault();
        if (anchor.Year == 0)
            throw new InvalidOperationException(
                "Cannot Generate Volume Group: dbo.LPM_OTS_Output has no rows (no anchor wk).");
        var aY = anchor.Year;
        var aW = anchor.Week;
        var anchorKey = aY * 100 + aW;

        // 1c) Refresh MonthlyWeightage on every LPM_Weekly_SalesAmt row from
        //     LPM_WeeklyWeights (per-week WeightPct, keyed by (Country, Year, Week))
        //     so the stored value stays a single-source recon check for the current
        //     Generate run. Rows without a matching (Year, Week) config row get
        //     MonthlyWeightage = NULL and drop out of the SUM below. Country
        //     filter locks to 'UAE' — the sole country populated in the current
        //     config; extend here if per-country rules land later. UpdatedTS
        //     stamped in GST.
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE ws
               SET MonthlyWeightage = ww.WeightPct,
                   UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
              FROM dbo.LPM_Weekly_SalesAmt ws
              LEFT JOIN dbo.LPM_WeeklyWeights ww WITH (NOLOCK)
                     ON ww.Country = 'UAE'
                    AND ww.Year1   = ws.Year1
                    AND ww.Week    = ws.Week",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 1d) Per (StoreID, DivCode), sum SalesAmt * MonthlyWeightage over
        //     the up-to-12 most recent LPM_Weekly_SalesAmt rows at/before the
        //     anchor. MonthlyWeightage is now the per-week WeightPct from
        //     LPM_WeeklyWeights (populated by 1c above). Since 12 weeks of
        //     weights are configured to sum to 1.0, this SUM directly yields
        //     the weighted-average monthly sales.
        //
        //     Sort key is (Year1 DESC, Week DESC) to match wk being a
        //     fiscal-year-resetting week number. Stores with fewer than 12
        //     rows in the window use whatever's available (per ops guidance
        //     while the weekly-sales history is still being backfilled).
        var weightedRows = (await c.QueryAsync<(string StoreID, int DivCode, int WeekCount, decimal MonthlySalesAmt)>(new CommandDefinition(@"
            ;WITH ranked AS (
                SELECT StoreID, DivCode, SalesAmt, MonthlyWeightage,
                       ROW_NUMBER() OVER (
                           PARTITION BY StoreID, DivCode
                           ORDER BY Year1 DESC, Week DESC
                       ) AS rn
                  FROM dbo.LPM_Weekly_SalesAmt WITH (NOLOCK)
                 WHERE (Year1 * 100 + Week) <= @anchorKey
            )
            SELECT StoreID, DivCode,
                   COUNT(*) AS WeekCount,
                   CAST(SUM(
                       CAST(ISNULL(SalesAmt, 0) AS DECIMAL(18,2)) *
                       CAST(MonthlyWeightage AS DECIMAL(9,4))
                   ) AS DECIMAL(18,2)) AS MonthlySalesAmt
              FROM ranked
             WHERE rn <= 12
             GROUP BY StoreID, DivCode",
            new { anchorKey },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        var weightedByKey = weightedRows.ToDictionary(
            r => (r.StoreID, r.DivCode),
            r => r.MonthlySalesAmt);

        // 1d) Overlay MonthlySalesAmt onto baseRows as the SalesAmt used by
        //     the grade math below. Stores with no weekly rows at all use 0
        //     (they still get graded — usually to the lowest bucket).
        baseRows = baseRows.Select(r => (
            r.Country,
            r.StoreID,
            r.DivCode,
            SalesAmt: weightedByKey.TryGetValue((r.StoreID, r.DivCode), out var m) ? (decimal?)m : 0m
        )).ToList();

        // 2) Non-special (B..I) grade bands, ordered widest first for stable matching.
        var bands = (await c.QueryAsync<(string VolumeGroup, decimal? FromPct, decimal? ToPct)>(new CommandDefinition(@"
            SELECT VolumeGroup, AvgSalesPctFrom, AvgSalesPctTo
              FROM dbo.LPM_VolumeGroupRange WITH (NOLOCK)
             WHERE IsSpecial = 0
             ORDER BY SortOrder",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        // 3) Compute avg per DivCode across all non-ECOM rows with SalesAmt > 0.
        var avgByDiv = baseRows
            .Where(r => !string.Equals(r.Country, "ECOM", StringComparison.OrdinalIgnoreCase)
                        && (r.SalesAmt ?? 0) > 0)
            .GroupBy(r => r.DivCode)
            .ToDictionary(g => g.Key, g => Math.Round(g.Average(x => x.SalesAmt ?? 0), 0, MidpointRounding.AwayFromZero));

        // 4) Assign grade per row.
        //    Grade A logic scoped per DivCode across all non-ECOM stores.
        var results = new List<StoreDivGradeRow>(baseRows.Count);

        foreach (var divGroup in baseRows.GroupBy(r => r.DivCode))
        {
            var avg = avgByDiv.TryGetValue(divGroup.Key, out var a) ? a : 0m;
            var nonEcom = divGroup
                .Where(r => !string.Equals(r.Country, "ECOM", StringComparison.OrdinalIgnoreCase))
                .Select(r => new
                {
                    r.Country, r.StoreID, r.DivCode, r.SalesAmt,
                    Pct = avg > 0 ? Math.Round((r.SalesAmt ?? 0) / avg * 100m, 0, MidpointRounding.AwayFromZero) : (decimal?)null
                })
                .OrderByDescending(x => x.Pct ?? 0)
                .ToList();

            var above300 = nonEcom.Count(x => (x.Pct ?? 0) > 300m);
            var aCount   = Math.Max(2, above300);
            var aStoreIds = new HashSet<string>(
                nonEcom.Take(aCount).Select(x => x.StoreID),
                StringComparer.OrdinalIgnoreCase);

            foreach (var r in divGroup)
            {
                string grade;
                decimal? pct = null;

                if (string.Equals(r.Country, "ECOM", StringComparison.OrdinalIgnoreCase))
                {
                    grade = "Z";
                }
                else
                {
                    pct = avg > 0 ? Math.Round((r.SalesAmt ?? 0) / avg * 100m, 0, MidpointRounding.AwayFromZero) : (decimal?)null;
                    if (aStoreIds.Contains(r.StoreID))
                    {
                        grade = "A";
                    }
                    else if (pct.HasValue)
                    {
                        var band = bands.FirstOrDefault(b =>
                            (!b.FromPct.HasValue || pct.Value >= b.FromPct.Value) &&
                            (!b.ToPct.HasValue   || pct.Value <= b.ToPct.Value));
                        grade = band.VolumeGroup ?? "";
                    }
                    else
                    {
                        grade = "";
                    }
                }

                results.Add(new StoreDivGradeRow(
                    Month1:      month,
                    Year1:       year,
                    Country:     r.Country,
                    StoreID:     r.StoreID,
                    StoreName:   null,
                    DivCode:     r.DivCode,
                    Division:    null,
                    SalesAmt:    r.SalesAmt,
                    AvgSalesAmt: avg > 0 ? avg : (decimal?)null,
                    AvgSalesPct: pct,
                    Grade:       grade));
            }
        }

        // 5) DELETE + bulk insert.
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.StoreDivGrade WHERE Month1 = @m AND Year1 = @y",
                new { m = month, y = year }, transaction: tx,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Month1",      typeof(int));
            dt.Columns.Add("Year1",       typeof(int));
            dt.Columns.Add("Country",     typeof(string));
            dt.Columns.Add("StoreID",     typeof(string));
            dt.Columns.Add("DivCode",     typeof(int));
            dt.Columns.Add("SalesAmt",    typeof(decimal));
            dt.Columns.Add("AvgSalesAmt", typeof(decimal));
            dt.Columns.Add("AvgSalesPct", typeof(decimal));
            dt.Columns.Add("Grade",       typeof(string));
            dt.Columns.Add("GeneratedBy", typeof(string));

            var who = user.Name ?? "";
            foreach (var r in results)
            {
                dt.Rows.Add(
                    r.Month1, r.Year1, r.Country, r.StoreID, r.DivCode,
                    (object?)r.SalesAmt    ?? DBNull.Value,
                    (object?)r.AvgSalesAmt ?? DBNull.Value,
                    (object?)r.AvgSalesPct ?? DBNull.Value,
                    (object?)r.Grade       ?? DBNull.Value,
                    who);
            }

            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.StoreDivGrade",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
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

        return results.Count;
    }

    /// <summary>Read persisted StoreDivGrade rows for a picked (Month, Year),
    /// joined to Division master + DataSettings for names. Used by the
    /// "View Volume Groups" dialog on the OTS PO Allocation page.</summary>
    public async Task<List<StoreDivGradeRow>> GetStoreDivGradesAsync(int month, int year, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<StoreDivGradeRow>(new CommandDefinition(@"
            ;WITH storeNames AS (
                SELECT StoreID, PBFullname,
                       rn = ROW_NUMBER() OVER (PARTITION BY StoreID ORDER BY PBFullname)
                  FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                 WHERE PBFullname IS NOT NULL AND LTRIM(RTRIM(PBFullname)) <> ''
            )
            SELECT g.Month1, g.Year1, g.Country, g.StoreID,
                   sn.PBFullname AS StoreName,
                   g.DivCode, dv.Division AS Division,
                   g.SalesAmt, g.AvgSalesAmt, g.AvgSalesPct, g.Grade
              FROM dbo.StoreDivGrade g WITH (NOLOCK)
              LEFT JOIN LPMSIM.dbo.Division dv WITH (NOLOCK) ON dv.DivCode = g.DivCode
              LEFT JOIN storeNames sn ON sn.StoreID = g.StoreID AND sn.rn = 1
             WHERE g.Month1 = @m AND g.Year1 = @y
             ORDER BY g.Country, g.StoreID, g.DivCode",
            new { m = month, y = year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
