using System.Text.RegularExpressions;
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

    /// <summary>Current fiscal week anchor from LPM_OTS_Output (latest OTSDate).
    /// This is TODAY's fiscal wk, which OTS Week Sales anchors on — the sum
    /// is (wk, wk+1, ..., wk+N-1) starting from this anchor. Nullable when
    /// LPM_OTS_Output has no wk data. Falls back to MIN(wk) from
    /// lpm_salestgtwk_stores if unavailable.
    /// Used by the OTS page for the Week Sales header + cell tooltips so the
    /// wk shown matches the wk the algorithm actually summed. month/year
    /// params kept for API compat; only the fallback path uses them.</summary>
    public async Task<int?> GetCurrentOtsWkAsync(int? month = null, int? year = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Preferred source: today's fiscal wk from LPM_OTS_Output.
        var wk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT TOP 1 wk FROM dbo.LPM_OTS_Output WITH (NOLOCK)
             WHERE wk IS NOT NULL
             ORDER BY OTSDate DESC, wk DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (wk is int v && v > 0) return v;

        // Fallback: earliest wk in lpm_salestgtwk_stores for the picked month.
        return await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT MIN(s.wk)
              FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
              JOIN dbo.LPM_EOM_Output e WITH (NOLOCK)
                ON e.StoreID = s.StoreID AND e.DivCode = s.DivCode
             WHERE (@m IS NULL OR e.Month1 = @m)
               AND (@y IS NULL OR e.Year1  = @y)
               AND s.wk IS NOT NULL",
            new { m = month, y = year },
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
                   TgtEOMMonth,
                   TgtEOM, SOHToday, NoOfLeadWeeks, WeekSales,
                   ISNULL(LeadIntransit, 0) AS LeadIntransit,
                   ISNULL(LeadDCSOH,     0) AS LeadDCSOH,
                   InTransit, Ex2DcSoh,
                   CountingWIP, OtsQtyToday, OtsPercentToday,
                   PrevEOMMonth,
                   ISNULL(PrevMonthEOM,    0) AS PrevMonthEOM,
                   ISNULL(WeekAdjustment,  0) AS WeekAdjustment,
                   ISNULL(CurrentEOW, TgtEOM) AS CurrentEOW
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
            TgtEOMMonth: r.TgtEOMMonth,
            TgtEOM: r.TgtEOM, SOHToday: r.SOHToday, NoOfLeadWeeks: r.NoOfLeadWeeks,
            WeekSales: r.WeekSales,
            LeadIntransit: r.LeadIntransit, LeadDCSOH: r.LeadDCSOH,
            InTransit: r.InTransit, Ex2DcSoh: r.Ex2DcSoh,
            CountingWIP: r.CountingWIP, OtsQtyToday: r.OtsQtyToday,
            OtsPercentToday: (double)r.OtsPercentToday,
            PrevEOMMonth: r.PrevEOMMonth,
            PrevMonthEOM: r.PrevMonthEOM,
            WeekAdjustment: r.WeekAdjustment,
            CurrentEOW: r.CurrentEOW)).ToList();
    }

    /// <summary>Runs the full compute for (Month, Year) across all countries and
    /// persists to dbo.WmsOtsPoAllocationRun stamped with OTSDate=today (GST).
    /// Any prior rows for the SAME OTSDate are DELETEd first so re-running
    /// on the same day replaces itself but keeps prior days intact. Callers
    /// should follow with LoadPersistedAsync. Only valid when Country=BFLGroup;
    /// the razor page enforces that.</summary>
    /// <summary>
    /// True when dbo.StoreDivGrade holds rows stamped with today's GST date — i.e.
    /// a BFLGROUP Volume Group run has already happened today. Only a BFLGROUP run
    /// writes that table; per-country runs go to LPM_StoreDivGrade_Country.
    ///
    /// GenerateAndPersistAsync enforces this as a hard precondition. Scheduled
    /// callers check it up front so they can defer instead of logging a failure
    /// they already know is coming.
    /// </summary>
    public async Task<bool> IsVolumeGroupGeneratedTodayAsync(CancellationToken ct = default)
    {
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        await using var chk = OpenOnPremBackup();
        var vgToday = await chk.ExecuteScalarAsync<int>(new CommandDefinition(@"
            SELECT COUNT(1) FROM dbo.StoreDivGrade WITH (NOLOCK)
             WHERE CAST(GeneratedTS AS DATE) = @dt",
            new { dt = todayGst }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return vgToday > 0;
    }

    /// <param name="actor">See GenerateStoreDivGradesAsync — scheduled callers pass
    /// an explicit value so the run is not audited as "anonymous".</param>
    public async Task<(int RowsPersisted, List<string> Warnings)> GenerateAndPersistAsync(
        int month, int year, CancellationToken ct = default, string? actor = null)
    {
        // Precondition: Volume Group must have been (re-)generated today (GST).
        // Enforces the daily refresh chain so OTS numbers don't ride on a stale
        // StoreDivGrade snapshot.
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        if (!await IsVolumeGroupGeneratedTodayAsync(ct))
            throw new InvalidOperationException(
                $"Volume Group has not been generated today ({todayGst:dd/MM/yyyy} GST). " +
                "Click 'Generate Volume Group' first, then re-run 'Generate' on OTS for PO Allocation.");

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
            dt.Columns.Add("TgtEOMMonth",     typeof(string));
            dt.Columns.Add("TgtEOM",          typeof(int));
            dt.Columns.Add("SOHToday",        typeof(int));
            dt.Columns.Add("NoOfLeadWeeks",   typeof(int));
            dt.Columns.Add("WeekSales",       typeof(int));
            dt.Columns.Add("LeadIntransit",   typeof(int));
            dt.Columns.Add("LeadDCSOH",       typeof(int));
            dt.Columns.Add("InTransit",       typeof(int));
            dt.Columns.Add("Ex2DcSoh",        typeof(int));
            dt.Columns.Add("CountingWIP",     typeof(int));
            dt.Columns.Add("OtsQtyToday",     typeof(int));
            dt.Columns.Add("OtsPercentToday", typeof(decimal));
            dt.Columns.Add("PrevEOMMonth",    typeof(string));
            dt.Columns.Add("PrevMonthEOM",    typeof(int));
            dt.Columns.Add("WeekAdjustment",  typeof(decimal));
            dt.Columns.Add("CurrentEOW",      typeof(int));

            var who = actor ?? user.Name ?? "";
            foreach (var r in rows)
            {
                dt.Rows.Add(
                    nowGst, who, month, year, otsDate,
                    r.Country, r.StoreID, (object?)r.StoreName ?? DBNull.Value,
                    r.DivCode, (object?)r.Division ?? DBNull.Value,
                    (object?)r.VolumeGroup ?? DBNull.Value,
                    (object?)r.PriorityRank ?? DBNull.Value,
                    (object?)r.TgtEOMMonth ?? DBNull.Value,
                    r.TgtEOM, r.SOHToday, r.NoOfLeadWeeks, r.WeekSales,
                    r.LeadIntransit, r.LeadDCSOH,
                    r.InTransit, r.Ex2DcSoh, r.CountingWIP, r.OtsQtyToday,
                    (decimal)r.OtsPercentToday,
                    (object?)r.PrevEOMMonth ?? DBNull.Value,
                    r.PrevMonthEOM, r.WeekAdjustment, r.CurrentEOW);
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
                    ISNULL(w.Weeks, 1) AS NoOfLeadWeeks,
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
        var weeksByCountry = baseRows.GroupBy(b => b.Country).ToDictionary(g => g.Key, g => g.First().NoOfLeadWeeks);

        // PER-COUNTRY TARGET EOM MONTH
        // Each country's "last week of sales" (currentWk + N - 1) can fall into
        // a calendar month AFTER the picked month. E.g. Qatar N=3, currentWk=30
        // -> weeks 30/31/32; wk 32 lies in August, so TgtEOM must be read from
        // August's LPM_EOM_Output row, not July's. PrevMonthEOM follows TgtEOM's
        // month - 1. Falls back to picked (month, year) when the country has no
        // WmsCountryOtsWeeks config or MFP has no calendar entry for lastWk.
        //
        // weeksInPrevMonthByCountry drives the WeekAdjustment denominator
        // downstream — its value is #weeks in the country's PrevEOMMonth (not
        // the picked month) so the per-week walk from PrevMonthEOM to TgtEOM
        // is calibrated to the correct calendar month.
        var weeksInPrevMonthByCountry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        {
            await using var c = OpenOnPremBackup();
            var currentWk = (await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT TOP 1 wk FROM dbo.LPM_OTS_Output WITH (NOLOCK)
                 WHERE wk IS NOT NULL
                 ORDER BY OTSDate DESC, wk DESC",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))) ?? 0;

            var fiscalCal = (await c.QueryAsync<(int Year, int Month, int Week)>(new CommandDefinition(@"
                SELECT DISTINCT [year] AS [Year], [month] AS [Month], [week] AS [Week]
                  FROM dbo.BFL_MFP_OUTBOUND_T1 WITH (NOLOCK)",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .GroupBy(r => (r.Year, r.Week))
                .ToDictionary(g => g.Key, g => (g.First().Month, g.First().Year));

            var pickedLabel     = new DateTime(year, month, 1).ToString("MMM-yyyy");
            var pickedPrevMonth = month == 1 ? 12 : month - 1;
            var pickedPrevYear  = month == 1 ? year - 1 : year;
            var pickedPrevLabel = new DateTime(pickedPrevYear, pickedPrevMonth, 1).ToString("MMM-yyyy");

            // Per country -> (TgtMonth, TgtYear, PrevMonth, PrevYear, TgtLabel, PrevLabel)
            var targetByCountry = new Dictionary<string, (int TgtMonth, int TgtYear, int PrevMonth, int PrevYear, string TgtLabel, string PrevLabel)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cty, n) in weeksByCountry)
            {
                var lastWk = currentWk + n - 1;
                int tgtM, tgtY;
                if (currentWk > 0 && fiscalCal.TryGetValue((year, lastWk), out var mm))
                {
                    tgtM = mm.Month; tgtY = mm.Year;
                }
                else if (currentWk > 0 && fiscalCal.TryGetValue((year + 1, lastWk - 52), out var mmn))
                {
                    tgtM = mmn.Month; tgtY = mmn.Year;
                }
                else
                {
                    tgtM = month; tgtY = year;
                }
                var prevM = tgtM == 1 ? 12 : tgtM - 1;
                var prevY = tgtM == 1 ? tgtY - 1 : tgtY;
                targetByCountry[cty] = (
                    tgtM, tgtY, prevM, prevY,
                    new DateTime(tgtY, tgtM, 1).ToString("MMM-yyyy"),
                    new DateTime(prevY, prevM, 1).ToString("MMM-yyyy"));
            }

            // Fetch EOM lookup once for all rows in the needed range (all countries
            // if filter is null, else just the picked country).
            var eomLookup = (await c.QueryAsync<(string StoreID, int DivCode, int Month1, int Year1, int TargetEOM)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, Month1, Year1, ISNULL(TargetEOM, 0) AS TargetEOM
                  FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE (@ct IS NULL OR Country = @ct)",
                new { ct = filter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .GroupBy(r => (r.StoreID, r.DivCode, r.Month1, r.Year1))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.TargetEOM));

            foreach (var row in baseRows)
            {
                if (targetByCountry.TryGetValue(row.Country, out var t))
                {
                    row.TgtEOMMonth  = t.TgtLabel;
                    row.TgtEOM       = eomLookup.TryGetValue((row.StoreID, row.DivCode, t.TgtMonth, t.TgtYear), out var teom) ? teom : row.TgtEOM;
                    row.PrevEOMMonth = t.PrevLabel;
                    row.PrevMonthEOM = eomLookup.TryGetValue((row.StoreID, row.DivCode, t.PrevMonth, t.PrevYear), out var peom) ? peom : row.PrevMonthEOM;
                }
                else
                {
                    row.TgtEOMMonth  = pickedLabel;
                    row.PrevEOMMonth = pickedPrevLabel;
                }
            }

            // Populate weeksInPrevMonthByCountry: count DISTINCT week per (month, year)
            // in the fiscal calendar (BFL_MFP_OUTBOUND_T1 derived), keyed by the
            // country's PREV EOM month. Used downstream as the WeekAdjustment
            // denominator so the per-week walk uses the correct calendar month's
            // week count.
            var weeksByMonthYear = fiscalCal.Values
                .GroupBy(mm => (mm.Month, mm.Year))
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var (cty, t) in targetByCountry)
            {
                weeksInPrevMonthByCountry[cty] = weeksByMonthYear.TryGetValue((t.PrevMonth, t.PrevYear), out var w) ? w : 0;
            }
        }

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

        // 3b) LeadIntransit + LeadDCSOH per (Country, DivCode) — filtered by a
        //     per-country LPMDt cutoff = 1st of the month that
        //     (today + LeadWeeks*7 days) lands in. Executed once per country
        //     because the cutoff (and, for LeadDCSOH, the source DB name) is
        //     country-specific. Same UAE=0 policy as InTransit.
        //
        //     LeadIntransit source: P2EXPORT..vTransferDetail JOIN
        //         datareporting.dbo.vupc_subclass, restricted to trfno in
        //         racks..InTransit_ExportShipment WHERE country=@ct AND intransit='Y'.
        //     LeadDCSOH source: [{DataName}]..WHBoxItemsExport JOIN vupc_subclass,
        //         DataName looked up in bfldata.dbo.DataSettings per country.
        var leadTask = SafeAsync(warnings, "LeadIntransit + LeadDCSOH", async () =>
        {
            var todayGst = DateTime.UtcNow.AddHours(4).Date;
            var perCountryCutoff = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cty, weeks) in weeksByCountry)
            {
                var landing = todayGst.AddDays(weeks * 7);
                perCountryCutoff[cty] = new DateTime(landing.Year, landing.Month, 1);
            }
            var leadDict = new Dictionary<(string Country, int DivCode), (int LeadIntransit, int LeadDcSoh)>();

            await using var c = OpenOnPremBackup();
            // DataName per country for the LeadDCSOH per-country query.
            var dataNames = (await c.QueryAsync<(string Country, string DataName)>(new CommandDefinition(@"
                SELECT UPPER(LTRIM(RTRIM(Country))) AS Country, DataName
                  FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                 WHERE DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .GroupBy(r => r.Country)
                .ToDictionary(g => g.Key, g => g.First().DataName, StringComparer.OrdinalIgnoreCase);

            foreach (var (ctyRaw, cutoff) in perCountryCutoff)
            {
                var cty = (ctyRaw ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(cty)) continue;
                // Skip countries that don't need Lead InTransit / Lead DC SOH:
                //   UAE, OMAN  -- excluded per operator spec (no lead qty tracked).
                //   ECOM (+ its ONLINE/ONLINEKSA data-name aliases) -- no physical
                //     transit + no country-DB WHBoxItemsExport table (would try
                //     [ONLINE]..WHBoxItemsExport / [DATA2004]..WHBoxItemsExport
                //     and hit "Invalid object name").
                // These countries fall through with 0 in both Lead columns.
                if (cty is "UAE" or "OMAN" or "ECOM" or "ONLINE" or "ONLINEKSA") continue;

                // LeadIntransit — single-DB query, filtered by racks..InTransit_ExportShipment.country.
                var itRows = await c.QueryAsync<(int DivCode, int Total)>(new CommandDefinition(@"
                    SELECT v.DivID AS DivCode, SUM(ISNULL(a.quantity, 0)) AS Total
                      FROM P2EXPORT..vTransferDetail a WITH (NOLOCK)
                      JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK) ON v.itemcode = a.ItemCode
                     WHERE a.trfno IN (
                         SELECT TrfNo FROM racks..InTransit_ExportShipment WITH (NOLOCK)
                          WHERE country = @cty AND intransit = 'Y')
                       AND a.lpmdt <= @cutoff
                       AND v.DivID IS NOT NULL
                     GROUP BY v.DivID",
                    new { cty, cutoff },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in itRows)
                    leadDict[(cty, r.DivCode)] = (r.Total, 0);

                // LeadDCSOH — per-country DB name lookup, skip when DataName missing.
                if (!dataNames.TryGetValue(cty, out var dataName)) continue;
                if (!Regex.IsMatch(dataName, @"^[A-Za-z0-9_]+$")) continue;
                var dcRows = await c.QueryAsync<(int DivCode, int Total)>(new CommandDefinition($@"
                    SELECT v.DivID AS DivCode, SUM(ISNULL(w.Qty, 0)) AS Total
                      FROM [{dataName}]..WHBoxItemsExport w WITH (NOLOCK)
                      JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK) ON v.itemcode = w.ItemCode
                     WHERE w.LPMDt <= @cutoff
                       AND v.DivID IS NOT NULL
                     GROUP BY v.DivID",
                    new { cutoff },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in dcRows)
                {
                    var prev = leadDict.TryGetValue((cty, r.DivCode), out var existing) ? existing : (0, 0);
                    leadDict[(cty, r.DivCode)] = (prev.Item1, r.Total);
                }
            }
            return leadDict;
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

            // Anchor at today's fiscal wk from LPM_OTS_Output (matches what the
            // tooltip + downstream code call "current wk"). If unavailable, fall
            // back to the first wk found in lpm_salestgtwk_stores for the month.
            var currentWk = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT TOP 1 wk FROM dbo.LPM_OTS_Output WITH (NOLOCK)
                 WHERE wk IS NOT NULL
                 ORDER BY OTSDate DESC, wk DESC",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var rows = await c.QueryAsync<(string StoreID, int DivCode, string Country, int Wk, int Sales)>(new CommandDefinition(@"
                SELECT s.StoreID, s.DivCode, e.Country, s.wk AS Wk, ISNULL(s.SalesTgtWk, 0) AS Sales
                  FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
                  JOIN dbo.LPM_EOM_Output e WITH (NOLOCK)
                    ON e.StoreID = s.StoreID AND e.DivCode = s.DivCode
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND (@ct IS NULL OR e.Country = @ct)",
                new { month, year, ct = filter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            // Anchor = current fiscal wk from LPM_OTS_Output; fall back to
            // MIN(wk) in the pulled rows if LPM_OTS_Output has no data.
            var minWk = currentWk is int cw && cw > 0
                ? cw
                : (rows.Any() ? rows.Min(r => r.Wk) : 0);
            var maxWkPerCountry = rows.Any()
                ? rows.GroupBy(r => r.Country).ToDictionary(
                    g => g.Key,
                    g => minWk + (weeksByCountry.TryGetValue(g.Key, out var w) ? w : 1) - 1)
                : new Dictionary<string, int>();

            // Per-store WeekSales — sum wk in [minWk, minWk + N) using
            // lpm_salestgtwk_stores as the per-store shape.
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

            // Rescale so country-x-div sums match MFP totals exactly (largest-remainder).
            // Per-store Math.Round drifted the sum by ~0.03% (78/285k on UAE wk 29);
            // floor+distribute-remainder eliminates that drift and makes each
            // (country, div) sum equal Math.Round(mfpTotal).
            var storeCountryDiv = existing.Keys
                .ToDictionary(k => k, k => rows.First(r => r.StoreID == k.StoreID && r.DivCode == k.DivCode).Country);
            var byCountryDiv = existing
                .GroupBy(kv => (Cty: storeCountryDiv[kv.Key], Div: kv.Key.DivCode))
                .ToDictionary(g => g.Key, g => g.ToList());

            var scaled = new Dictionary<(string StoreID, int DivCode), int>(existing.Count);
            foreach (var group in byCountryDiv)
            {
                var (cty, div) = group.Key;
                var members = group.Value;

                if (!mfpTotals.TryGetValue((cty, div), out var mfp) || mfp <= 0)
                {
                    // MFP has no data for this country-x-div -> keep existing values.
                    foreach (var kv in members) scaled[kv.Key] = kv.Value;
                    continue;
                }

                var existingCd = existingByCountryDiv.GetValueOrDefault((cty, div), 0);
                if (existingCd <= 0)
                {
                    // No lpm share to distribute against -> keep existing (usually 0).
                    foreach (var kv in members) scaled[kv.Key] = kv.Value;
                    continue;
                }

                var target = (int)Math.Round((double)mfp);
                var scale = (double)mfp / existingCd;

                // Floor per store; track fractional remainders for the largest-remainder pass.
                var floors = new int[members.Count];
                var remainders = new double[members.Count];
                var floorSum = 0;
                for (var i = 0; i < members.Count; i++)
                {
                    var raw = members[i].Value * scale;
                    floors[i] = (int)Math.Floor(raw);
                    remainders[i] = raw - floors[i];
                    floorSum += floors[i];
                }

                // Distribute the difference (target - floorSum) one unit at a time
                // to the stores with the largest remainders (or smallest if diff is negative).
                var diff = target - floorSum;
                if (diff != 0)
                {
                    var order = Enumerable.Range(0, members.Count)
                        .OrderByDescending(i => diff > 0 ? remainders[i] : -remainders[i])
                        .ToArray();
                    var step = diff > 0 ? 1 : -1;
                    var remaining = Math.Abs(diff);
                    for (var j = 0; j < order.Length && remaining > 0; j++)
                    {
                        floors[order[j]] += step;
                        remaining--;
                    }
                }

                for (var i = 0; i < members.Count; i++)
                    scaled[members[i].Key] = floors[i];
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

        await Task.WhenAll(sohTask, ex2Task, leadTask, weekSalesTask, storeCountTask, wipTask);
        var sohByKey            = sohTask.Result;
        var ex2ByKey            = ex2Task.Result;
        var leadByKey           = leadTask.Result;
        var weekSalesByKey      = weekSalesTask.Result;
        var storeCountByCountry = storeCountTask.Result;
        var wipByKey            = wipTask.Result;

        // 7) Merge + compute OTS Qty / OTS %.
        //
        // OTS uses CurrentEOW (interpolated end-of-week target) in place of
        // TgtEOM per current spec:
        //   WkReduction = (PrevMonthEOM - TgtEOM) / weeksInMonth
        //   CurrentEOW  = PrevMonthEOM - (WkReduction * weeksElapsedSoFar)
        //   OTS Qty     = CurrentEOW + WeekSales - SOH - LeadIntransit - LeadDCSOH
        //   OTS %       = OTS Qty / CurrentEOW * 100  (0 when CurrentEOW <= 0)
        // Only supply arriving inside the country's lead window counts (Lead
        // InTransit + Lead DC SOH). For countries with no Lead tracking (UAE,
        // OMAN, ECOM) both Leads are 0 -> OTS = CurrentEOW + WeekSales - SOH.
        // InTransit / Ex2DcSoh / CountingWIP columns remain on the grid for
        // reference but no longer feed the OTS shortfall.
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
        // Count distinct fiscal weeks that fall inside the picked (Month, Year)
        // directly from BFL_MFP_OUTBOUND_T1. MFP already carries (month, year, week)
        // at the row grain, so DISTINCT(week) filtered by the picked month/year
        // gives the exact number of fiscal weeks in that month (~4-5), not the
        // full-history window the old lpm_salestgtwk_stores join was returning.
        var weeksInMonth = await SafeAsync(warnings, "weeksInMonth (BFL_MFP_OUTBOUND_T1)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var v = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT COUNT(DISTINCT week)
                  FROM dbo.BFL_MFP_OUTBOUND_T1 WITH (NOLOCK)
                 WHERE month = @month AND year = @year",
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
        // Largest-remainder allocation for InTransit + Ex2 DC SOH so per-row
        // amounts sum exactly to the (Country, Div) totals from LPM_Ex2ItemSOH
        // (no integer-division loss). Prior code did `total / country_storeCount`
        // per row, which lost up to N-1 units per (Country, Div) pair; across
        // ~7 countries x ~15 divs the ribbon under-counted by ~0.1%.
        //
        // Divisor is now the count of rows in this (Country, Div) that actually
        // exist in baseRows (i.e. have EOM data for the picked Month/Year) so
        // the leftover is guaranteed to reach a real store. UAE is skipped for
        // InTransit per prior spec.
        var perRowInTransit     = new Dictionary<(string Country, string StoreID, int DivCode), int>();
        var perRowEx2Dc         = new Dictionary<(string Country, string StoreID, int DivCode), int>();
        var perRowLeadIntransit = new Dictionary<(string Country, string StoreID, int DivCode), int>();
        var perRowLeadDcSoh     = new Dictionary<(string Country, string StoreID, int DivCode), int>();
        foreach (var group in baseRows.GroupBy(b => ((b.Country ?? "").Trim().ToUpperInvariant(), b.DivCode)))
        {
            var (cKey, divCode) = group.Key;
            var haveEx2 = ex2ByKey.TryGetValue(group.Key, out var totals);
            var haveLead = leadByKey.TryGetValue(group.Key, out var leadTotals);
            if (!haveEx2 && !haveLead) continue;
            var (inTransitTotal, ex2DcTotal) = haveEx2 ? totals : (0, 0);
            var (leadInTotal, leadDcTotal)   = haveLead ? leadTotals : (0, 0);
            var isUae = string.Equals(cKey, "UAE", StringComparison.OrdinalIgnoreCase);
            var effectiveInTransit     = isUae ? 0 : inTransitTotal;
            var effectiveLeadIntransit = isUae ? 0 : leadInTotal;    // UAE=0, same policy
            var storesInGroup = group.ToList();
            var n = storesInGroup.Count;
            if (n == 0) continue;
            // Sort deterministically so leftover units land consistently.
            storesInGroup.Sort((a, b) => string.CompareOrdinal(a.StoreID, b.StoreID));
            var itFloor  = effectiveInTransit     / n;
            var itRem    = effectiveInTransit     % n;
            var e2Floor  = ex2DcTotal             / n;
            var e2Rem    = ex2DcTotal             % n;
            var liFloor  = effectiveLeadIntransit / n;
            var liRem    = effectiveLeadIntransit % n;
            var ldFloor  = leadDcTotal            / n;
            var ldRem    = leadDcTotal            % n;
            for (int i = 0; i < n; i++)
            {
                var s = storesInGroup[i];
                var key = (s.Country, s.StoreID, s.DivCode);
                perRowInTransit[key]     = itFloor + (i < itRem ? 1 : 0);
                perRowEx2Dc[key]         = e2Floor + (i < e2Rem ? 1 : 0);
                perRowLeadIntransit[key] = liFloor + (i < liRem ? 1 : 0);
                perRowLeadDcSoh[key]     = ldFloor + (i < ldRem ? 1 : 0);
            }
        }

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
            var ws        = weekSalesByKey.TryGetValue((r.StoreID, r.DivCode), out var w) ? w : 0;
            var inTransit    = perRowInTransit.TryGetValue((r.Country, r.StoreID, r.DivCode), out var itPer)     ? itPer     : 0;
            var ex2dc        = perRowEx2Dc.TryGetValue((r.Country, r.StoreID, r.DivCode), out var e2Per)         ? e2Per     : 0;
            var leadIntransit= perRowLeadIntransit.TryGetValue((r.Country, r.StoreID, r.DivCode), out var liPer) ? liPer     : 0;
            var leadDcSoh    = perRowLeadDcSoh.TryGetValue((r.Country, r.StoreID, r.DivCode), out var ldPer)     ? ldPer     : 0;
            var wip = wipByKey.TryGetValue((r.Country, r.StoreID, r.DivCode), out var v) ? v : 0;

            // WeekAdjustment = per-week walk rate from PrevMonthEOM toward TgtEOM,
            // sized to the # weeks in the PREV EOM month (per country). Positive
            // when scaling up (Tgt > Prev), negative when winding down.
            // CurrentEOW = PrevMonthEOM + WeekAdjustment × NoOfLeadWeeks — the
            // interpolation projects forward by the country's lead-weeks value.
            var wpm = weeksInPrevMonthByCountry.TryGetValue(r.Country, out var wp) && wp > 0
                ? wp
                : (weeksInMonth > 0 ? weeksInMonth : 0);
            decimal weekAdjustment;
            int currentEOW;
            if (r.PrevMonthEOM > 0 && wpm > 0)
            {
                weekAdjustment = (decimal)(r.TgtEOM - r.PrevMonthEOM) / wpm;
                currentEOW     = (int)Math.Round(r.PrevMonthEOM + weekAdjustment * r.NoOfLeadWeeks);
            }
            else
            {
                weekAdjustment = 0m;
                currentEOW     = r.TgtEOM;   // fall back when no previous history
            }

            // OTS formula (v1.0.364+): subtract the LEAD-horizon-filtered
            // supply (Lead InTransit + Lead DC SOH) instead of the raw totals.
            // Only stock that arrives within the country's lead window counts
            // toward meeting Current EOW. For countries excluded from the Lead
            // prefetch (UAE, OMAN, ECOM), leadIntransit + leadDcSoh = 0 so the
            // formula reduces to (CurrentEOW + WeekSales - SOH).
            var otsQty = currentEOW + ws - soh - leadIntransit - leadDcSoh;
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
                TgtEOMMonth:     r.TgtEOMMonth,
                TgtEOM:          r.TgtEOM,
                SOHToday:        soh,
                NoOfLeadWeeks:   r.NoOfLeadWeeks,
                WeekSales:       ws,
                LeadIntransit:   leadIntransit,
                LeadDCSOH:       leadDcSoh,
                InTransit:       inTransit,
                Ex2DcSoh:        ex2dc,
                CountingWIP:     wip,
                OtsQtyToday:     otsQty,
                OtsPercentToday: otsPct,
                PrevEOMMonth:    r.PrevEOMMonth,
                PrevMonthEOM:    r.PrevMonthEOM,
                WeekAdjustment:  weekAdjustment,
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
        public string?  TgtEOMMonth    { get; set; }
        public int      TgtEOM         { get; set; }
        public int      NoOfLeadWeeks  { get; set; }
        public string?  PrevEOMMonth   { get; set; }
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
        public string?  TgtEOMMonth     { get; set; }
        public int      TgtEOM          { get; set; }
        public int      SOHToday        { get; set; }
        public int      NoOfLeadWeeks   { get; set; }
        public int      WeekSales       { get; set; }
        public int      LeadIntransit   { get; set; }
        public int      LeadDCSOH       { get; set; }
        public int      InTransit       { get; set; }
        public int      Ex2DcSoh        { get; set; }
        public int      CountingWIP     { get; set; }
        public int      OtsQtyToday     { get; set; }
        public decimal  OtsPercentToday { get; set; }
        public string?  PrevEOMMonth    { get; set; }
        public int      PrevMonthEOM    { get; set; }
        public decimal  WeekAdjustment  { get; set; }
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
    //       Rest                                -> LPM_VolumeGroupRange_Country band lookup
    //                                              (IsSpecial = 0), matched on DivCode
    // Idempotent: DELETE existing rows for (Month, Year) then bulk insert.
    // ==============================================================
    /// <param name="actor">
    /// Who to stamp into StoreDivGrade.GeneratedBy. Scheduled callers pass an
    /// explicit value — ICurrentUser resolves to "anonymous" outside a Razor
    /// circuit, which is useless in an audit column. Null keeps the UI behaviour
    /// of reading the signed-in user.
    /// </param>
    /// <returns>
    /// (Rows persisted, Ungraded) — Ungraded counts non-ECOM stores that matched no
    /// band, i.e. rows written with a blank Grade. Non-zero means the band ranges
    /// for this country do not cover the full AvgSalesPct spread.
    /// </returns>
    public async Task<(int Rows, int Ungraded)> GenerateStoreDivGradesAsync(int month, int year, string? country = null, CancellationToken ct = default, string? actor = null)
    {
        // country == null OR BflGroup -> GLOBAL run (existing behaviour):
        //   bands from  dbo.LPM_VolumeGroupRange_Country WHERE Country = 'BFLGROUP'
        //   weights via dbo.LPM_WeeklyWeights WHERE Country = 'UAE'
        //   persist to  dbo.StoreDivGrade  (delete WHERE Month1=@m AND Year1=@y)
        // country == a specific country (e.g. 'BAHRAIN', 'KSA') -> PER-COUNTRY run:
        //   bands from  dbo.LPM_VolumeGroupRange_Country WHERE Country = @country
        //   weights via dbo.LPM_WeeklyWeights           WHERE Country = @country
        //   filter baseRows to that country only
        //   persist to  dbo.LPM_StoreDivGrade_Country
        //               (delete WHERE Country=@country AND Month1=@m AND Year1=@y)
        var isPerCountry = !string.IsNullOrWhiteSpace(country)
                           && !string.Equals(country, BflGroup, StringComparison.OrdinalIgnoreCase);
        var countryFilter = isPerCountry ? country!.Trim() : null;
        // Bands source is ALWAYS the per-country table now — old dbo.LPM_VolumeGroupRange
        // is deprecated. BFLGroup run reads the row where Country='BFLGROUP'.
        var bandsCountry  = isPerCountry ? countryFilter! : "BFLGROUP";
        var gradeTable    = isPerCountry ? "dbo.LPM_StoreDivGrade_Country"    : "dbo.StoreDivGrade";
        var weightsCountry = isPerCountry ? countryFilter! : "UAE";

        await using var c = OpenOnPremBackup();

        // 1a) Base set — one row per (Country, StoreID, DivCode) to grade.
        //     Country comes from LPM_EOM_Output; the sales amount used for
        //     grading is now sourced from the weighted weekly rollup below.
        //     Per-country run filters baseRows to the picked country.
        var baseRows = (await c.QueryAsync<(string Country, string StoreID, int DivCode, decimal? SalesAmt)>(new CommandDefinition($@"
            SELECT Country, StoreID, DivCode, CAST(NULL AS DECIMAL(18,2)) AS SalesAmt
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Month1 = @m AND Year1 = @y
               AND Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
               AND Country <> 'Ex2Locations'
               {(isPerCountry ? "AND Country = @ct" : "")}",
            new { m = month, y = year, ct = countryFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        if (baseRows.Count == 0) return (0, 0);

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
        //     Generate run. Rows without a matching (Country, Year, Week) config
        //     row get MonthlyWeightage = NULL and drop out of the SUM below.
        //     Country filter uses the picked country in a per-country run,
        //     'UAE' otherwise (BflGroup run — the sole country populated in the
        //     shared config). UpdatedTS stamped in GST.
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE ws
               SET MonthlyWeightage = ww.WeightPct,
                   UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
              FROM dbo.LPM_Weekly_SalesAmt ws
              LEFT JOIN dbo.LPM_WeeklyWeights ww WITH (NOLOCK)
                     ON ww.Country = @wcty
                    AND ww.Year1   = ws.Year1
                    AND ww.Week    = ws.Week",
            new { wcty = weightsCountry },
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
        //    Always sourced from LPM_VolumeGroupRange_Country now — BFLGroup uses
        //    the Country='BFLGROUP' rows, per-country runs use the picked country.
        //
        //    Bands are DIVISION-SPECIFIC: a division's own rows win, and rows with
        //    no DivCode act as the country-wide fallback for divisions that have
        //    none of their own. Previously every row for the country was thrown
        //    into one flat list, so with per-division bands loaded the first
        //    SortOrder match from ANY division decided the grade.
        var bandRows = (await c.QueryAsync<(int? DivCode, string VolumeGroup, decimal? FromPct, decimal? ToPct)>(
            new CommandDefinition(@"
            SELECT DivCode, VolumeGroup, AvgSalesPctFrom, AvgSalesPctTo
              FROM dbo.LPM_VolumeGroupRange_Country WITH (NOLOCK)
             WHERE IsSpecial = 0
               AND Country = @ct
             ORDER BY SortOrder",
            new { ct = bandsCountry },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        // Grading every store blank is indistinguishable from "the algorithm ran"
        // in the UI, and that silence cost real debugging time. Fail loudly instead.
        //
        // NOTE the guard is deliberately not just Count == 0. A PARTIAL band set
        // fails just as silently: BFLGROUP held a single row (DivCode 402, A,
        // 300-99999), so every store below 300% matched nothing and graded blank
        // while the page reported thousands of rows generated. The ungraded count
        // returned below is what actually surfaces that case.
        if (bandRows.Count == 0)
            throw new InvalidOperationException(
                $"No Volume Group bands configured for Country = '{bandsCountry}' in " +
                "dbo.LPM_VolumeGroupRange_Country (IsSpecial = 0). Load the band ranges " +
                "for this country before generating Volume Groups — without them every " +
                "store would be graded blank.");

        // GroupBy preserves source order, and the query is ordered by SortOrder,
        // so each division's list stays in SortOrder — first match wins, as before.
        var bandsByDiv = bandRows
            .Where(b => b.DivCode.HasValue)
            .GroupBy(b => b.DivCode!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var bandsFallback = bandRows.Where(b => !b.DivCode.HasValue).ToList();

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
                        // This division's own bands, else the country-wide (no DivCode)
                        // set. A division with neither still grades blank — same as
                        // before — but the empty-table case now throws up front.
                        var bandsForDiv = bandsByDiv.TryGetValue(r.DivCode, out var divBands)
                            ? divBands
                            : bandsFallback;
                        var band = bandsForDiv.FirstOrDefault(b =>
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
                $"DELETE FROM {gradeTable} WHERE Month1 = @m AND Year1 = @y" +
                (isPerCountry ? " AND Country = @ct" : ""),
                new { m = month, y = year, ct = countryFilter }, transaction: tx,
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

            var who = actor ?? user.Name ?? "";
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
                DestinationTableName = gradeTable,
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

        // Ungraded = non-ECOM rows whose AvgSalesPct matched no band. Callers show
        // this next to the row count so a band set that covers only part of the
        // percentage range is visible immediately, rather than looking like a
        // clean run until someone opens View Volume Groups.
        var ungraded = results.Count(r =>
            string.IsNullOrEmpty(r.Grade)
            && !string.Equals(r.Country, "ECOM", StringComparison.OrdinalIgnoreCase));

        return (results.Count, ungraded);
    }

    /// <summary>Read persisted StoreDivGrade rows for a picked (Month, Year),
    /// joined to Division master + DataSettings for names. Used by the
    /// "View Volume Groups" dialog on the OTS PO Allocation page.</summary>
    public async Task<List<StoreDivGradeRow>> GetStoreDivGradesAsync(int month, int year, string? country = null, CancellationToken ct = default)
    {
        // Country selector:
        //   null / BflGroup       -> read from dbo.StoreDivGrade (all countries)
        //   specific country      -> read from dbo.LPM_StoreDivGrade_Country WHERE Country = @c
        var isPerCountry = !string.IsNullOrWhiteSpace(country)
                           && !string.Equals(country, BflGroup, StringComparison.OrdinalIgnoreCase);
        var countryFilter = isPerCountry ? country!.Trim() : null;
        var gradeTable    = isPerCountry ? "dbo.LPM_StoreDivGrade_Country" : "dbo.StoreDivGrade";
        var countryClause = isPerCountry ? "AND g.Country = @ct" : "";

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<StoreDivGradeRow>(new CommandDefinition($@"
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
              FROM {gradeTable} g WITH (NOLOCK)
              LEFT JOIN LPMSIM.dbo.Division dv WITH (NOLOCK) ON dv.DivCode = g.DivCode
              LEFT JOIN storeNames sn ON sn.StoreID = g.StoreID AND sn.rn = 1
             WHERE g.Month1 = @m AND g.Year1 = @y
               {countryClause}
             ORDER BY g.Country, g.StoreID, g.DivCode",
            new { m = month, y = year, ct = countryFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
