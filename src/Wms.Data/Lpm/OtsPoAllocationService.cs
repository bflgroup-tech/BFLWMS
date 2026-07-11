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

    /// <summary>Load persisted rows for a (Month, Year), optionally filtered by
    /// country + a division subset. Country == null or BFLGroup means "all".
    /// divisions == null or empty means "all divisions".</summary>
    public async Task<List<OtsPoAllocationRow>> LoadPersistedAsync(
        int month, int year, string? country, IReadOnlyCollection<int>? divisions,
        CancellationToken ct = default)
    {
        var filter = string.IsNullOrWhiteSpace(country) || string.Equals(country, BflGroup, StringComparison.OrdinalIgnoreCase)
            ? null : country;
        var eomLabel = new DateTime(year, month, 1).ToString("MMM-yyyy");
        var divClause = divisions is { Count: > 0 } ? "AND DivCode IN @divs" : "";
        var sql = $@"
            SELECT Country, StoreID, StoreName, DivCode, Division, VolumeGroup, PriorityRank,
                   TgtEOM, SOHToday, WeeksToInclude, WeekSales, InTransit, Ex2DcSoh,
                   CountingWIP, OtsQtyToday, OtsPercentToday
              FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
             WHERE [Year] = @year AND [Month] = @month
               AND (@ct IS NULL OR Country = @ct)
               {divClause}
             ORDER BY Country, StoreID, DivCode";
        await using var c = OpenWms();
        var rows = await c.QueryAsync<PersistedRow>(new CommandDefinition(
            sql, new { month, year, ct = filter, divs = divisions?.ToArray() },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Select(r => new OtsPoAllocationRow(
            Country: r.Country, StoreID: r.StoreID, StoreName: r.StoreName,
            DivCode: r.DivCode, Division: r.Division, VolumeGroup: r.VolumeGroup,
            PriorityRank: r.PriorityRank, EOMMonth: eomLabel,
            TgtEOM: r.TgtEOM, SOHToday: r.SOHToday, WeeksToInclude: r.WeeksToInclude,
            WeekSales: r.WeekSales, InTransit: r.InTransit, Ex2DcSoh: r.Ex2DcSoh,
            CountingWIP: r.CountingWIP, OtsQtyToday: r.OtsQtyToday,
            OtsPercentToday: (double)r.OtsPercentToday)).ToList();
    }

    /// <summary>Runs the full compute for (Month, Year) across all countries and
    /// persists to dbo.WmsOtsPoAllocationRun. Any prior rows for the same
    /// (Month, Year) are DELETEd first. Callers should follow with LoadPersistedAsync.
    /// Only valid when Country=BFLGroup; the razor page enforces that.</summary>
    public async Task<(int RowsPersisted, List<string> Warnings)> GenerateAndPersistAsync(
        int month, int year, CancellationToken ct = default)
    {
        var (rows, warnings) = await GenerateAsync(month, year, country: null, ct);
        if (rows.Count == 0) return (0, warnings);

        await using var c = OpenWms();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                DELETE FROM dbo.WmsOtsPoAllocationRun
                 WHERE [Year] = @year AND [Month] = @month",
                new { month, year }, transaction: tx,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var dt = new System.Data.DataTable();
            dt.Columns.Add("RunTS",           typeof(DateTime));
            dt.Columns.Add("RunBy",           typeof(string));
            dt.Columns.Add("Month",           typeof(int));
            dt.Columns.Add("Year",            typeof(int));
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

            var nowGst = DateTime.UtcNow.AddHours(4);
            var who = user.Name ?? "";
            foreach (var r in rows)
            {
                dt.Rows.Add(
                    nowGst, who, month, year,
                    r.Country, r.StoreID, (object?)r.StoreName ?? DBNull.Value,
                    r.DivCode, (object?)r.Division ?? DBNull.Value,
                    (object?)r.VolumeGroup ?? DBNull.Value,
                    (object?)r.PriorityRank ?? DBNull.Value,
                    r.TgtEOM, r.SOHToday, r.WeeksToInclude, r.WeekSales,
                    r.InTransit, r.Ex2DcSoh, r.CountingWIP, r.OtsQtyToday,
                    (decimal)r.OtsPercentToday);
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
            baseRows = (await c.QueryAsync<BaseRow>(new CommandDefinition(@"
                SELECT
                    e.Country,
                    e.StoreID,
                    d.PBFullname AS StoreName,
                    e.DivCode,
                    dv.Division  AS Division,
                    e.VolumeGroup,
                    e.PriorityRank,
                    e.MerchNeedMonth AS TgtEOM,
                    ISNULL(w.Weeks, 1) AS WeeksToInclude
                  FROM dbo.LPM_EOM_Output e WITH (NOLOCK)
                  LEFT JOIN LPMSIM.dbo.Division dv WITH (NOLOCK) ON dv.DivCode = e.DivCode
                  LEFT JOIN bfldata.dbo.DataSettings d WITH (NOLOCK) ON d.StoreID = e.StoreID
                  LEFT JOIN dbo.WmsCountryOtsWeeks w WITH (NOLOCK) ON w.SimCountry = e.Country
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND e.Country <> 'Ex2Locations'
                   AND (@ct IS NULL OR e.Country = @ct)
                 ORDER BY e.Country, e.StoreID, e.DivCode",
                new { month, year, ct = filter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }

        if (baseRows.Count == 0) return (new(), warnings);

        // 2) SOH Today per (StoreID, DivCode) from Racks.dbo.LPM_Locstock.
        var sohByKey = await SafeAsync(warnings, "SOH Today (Racks.dbo.LPM_Locstock)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(string StoreID, int DivCode, int SOH)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, SUM(ISNULL(SOH, 0)) AS SOH
                  FROM Racks.dbo.LPM_Locstock WITH (NOLOCK)
                 GROUP BY StoreID, DivCode",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(r => (r.StoreID, r.DivCode), r => r.SOH);
        }, () => new Dictionary<(string, int), int>());

        // 3) Ex2 SOH pair per DivCode from LPM_Ex2ItemSOH (item-level).
        //
        // Ex2 warehouses live under DataSettings.SIMCountry='Ex2Locations' and
        // serve every retail country — so we sum across ALL Ex2 warehouses per
        // DivCode. The per-country split happens in the merge below by dividing
        // by that country's retail-store count.
        //   Itemcode -> datareporting.dbo.vupc_subclass.DivID (matches
        //               LPM_EOM_Output.DivCode's domain)
        var ex2ByDiv = await SafeAsync(warnings, "InTransit/Ex2 DC SOH (LPM_Ex2ItemSOH)", async () =>
        {
            await using var c = OpenOnPremBackup();
            var rows = await c.QueryAsync<(int DivCode, int InTransitTotal, int Ex2DcTotal)>(new CommandDefinition(@"
                SELECT v.DivID AS DivCode,
                       SUM(ISNULL(sohs.Ex2SOH, 0) + ISNULL(sohs.BoxSOH, 0)) AS InTransitTotal,
                       SUM(ISNULL(sohs.R1WHSOH, 0))                         AS Ex2DcTotal
                  FROM dbo.LPM_Ex2ItemSOH sohs WITH (NOLOCK)
                  JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                    ON v.itemcode = sohs.Itemcode
                 WHERE v.DivID IS NOT NULL
                 GROUP BY v.DivID",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.ToDictionary(r => r.DivCode, r => (r.InTransitTotal, r.Ex2DcTotal));
        }, () => new Dictionary<int, (int, int)>());

        // 4) Week Sales per (StoreID, DivCode) summed over next N weeks from currentWk.
        var weeksByCountry = baseRows.GroupBy(b => b.Country).ToDictionary(g => g.Key, g => g.First().WeeksToInclude);
        var weekSalesByKey = await SafeAsync(warnings, "Week Sales (lpm_salestgtwk_stores)", async () =>
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
        var storeCountByCountry = await SafeAsync(warnings, "Store count", async () =>
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
        var wipByKey = await SafeAsync(warnings, "Counting WIP (Azure)", async () =>
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

            HashSet<string> completedContnos;
            List<(string Country, string StoreID, int DivCode, int Qty, string ContNo)> allocs;
            await using (var w = OpenWms())
            {
                completedContnos = (await w.QueryAsync<string>(new CommandDefinition(@"
                    SELECT DISTINCT ContNo FROM dbo.WmsBuildingCompletion WITH (NOLOCK)",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                allocs = (await w.QueryAsync<(string Country, string StoreID, int DivCode, int Qty, string ContNo)>(
                    new CommandDefinition(@"
                        SELECT Country, StoreID, DivCode = ISNULL(DivCode, 0),
                               Qty = SUM(ISNULL(AllocatedQty, 0)), ContNo
                          FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                         WHERE StoreID IS NOT NULL
                         GROUP BY Country, StoreID, DivCode, ContNo",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
            }

            return allocs
                .Where(a => approvedContnos.Contains(a.ContNo) && !completedContnos.Contains(a.ContNo))
                .GroupBy(a => (a.Country ?? "", a.StoreID, a.DivCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        }, () => new Dictionary<(string, string, int), int>());

        // 7) Merge + compute OTS Qty / OTS %.
        var eomLabel = new DateTime(year, month, 1).ToString("MMM-yyyy");
        var results = new List<OtsPoAllocationRow>(baseRows.Count);
        foreach (var r in baseRows)
        {
            var soh    = sohByKey.TryGetValue((r.StoreID, r.DivCode), out var s) ? s : 0;
            var ws     = weekSalesByKey.TryGetValue((r.StoreID, r.DivCode), out var w) ? w : 0;
            var stores = storeCountByCountry.TryGetValue(r.Country, out var sc) ? sc : 0;
            int inTransit = 0, ex2dc = 0;
            if (ex2ByDiv.TryGetValue(r.DivCode, out var e) && stores > 0)
            {
                var (inTransitTotal, ex2DcTotal) = e;
                if (!string.Equals(r.Country, "UAE", StringComparison.OrdinalIgnoreCase))
                    inTransit = inTransitTotal / stores;
                ex2dc = ex2DcTotal / stores;
            }
            var wip    = wipByKey.TryGetValue((r.Country, r.StoreID, r.DivCode), out var v) ? v : 0;
            var otsQty = r.TgtEOM + ws - soh - inTransit - ex2dc - wip;
            var otsPct = r.TgtEOM > 0 ? (double)otsQty / r.TgtEOM * 100.0 : 0.0;
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
                OtsPercentToday: otsPct));
        }
        return (results, warnings);
    }

    private static async Task<T> SafeAsync<T>(List<string> warnings, string label, Func<Task<T>> body, Func<T> fallback)
    {
        try { return await body(); }
        catch (Exception ex)
        {
            warnings.Add($"{label} unavailable: {ex.Message}");
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
    }
}
