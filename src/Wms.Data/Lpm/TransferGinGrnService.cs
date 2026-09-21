using System.Text;
using System.Text.RegularExpressions;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Transfer / GIN / GRN History report.
///
/// Two data sources per non-UAE country:
///   - OnPremBackup (192.168.5.61) hosts a synced mirror of every country's data,
///     reachable via linked-server DataName references (e.g. [bflksa]..transferheader).
///     It can lag during the day, so it's only trusted for dates BEFORE today.
///   - The country's own regional server (KSA_DB_ConnectionString etc.) is the
///     live, authoritative source — used ONLY for today's slice of a date range,
///     to avoid double-counting rows a same-day sync may have already copied
///     into OnPremBackup.
/// UAE has no separate regional server at all — OnPremBackup IS its live system,
/// so UAE never splits by date; every UAE query goes through OnPremBackup.
/// </summary>
public class TransferGinGrnService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    // UAE has no dedicated connection string — always use OnPremBackup for it.
    private const string UaeCountry = "UAE";

    // Every non-UAE country's historical data now also flows through this ONE
    // OnPremBackup server (in addition to UAE's own always-on usage of it) —
    // "BFL Group" fans out per-country, and per-country fans out again per-shop,
    // so without a cap a single load can open hundreds of simultaneous
    // OnPremBackup connections at once, overwhelming it (slower, not faster).
    // Static: shared across every request/instance, since the thing being
    // protected is the shared server, not any one call. Mirrors the same fix
    // ShipmentStatusService already applies for its own OnPremBackup fan-out.
    private static readonly SemaphoreSlim OnPremThrottle = new(16);

    private SqlConnection OpenOnPrem()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    // Runs one OnPremBackup query under the shared throttle — the semaphore slot
    // is held for the connection's whole lifetime (acquired before it opens,
    // released only after it's disposed), so at most OnPremThrottle.CurrentCount
    // connections are ever open against OnPremBackup at once from this service.
    private async Task<T> WithOnPremAsync<T>(Func<SqlConnection, Task<T>> query, CancellationToken ct)
    {
        await OnPremThrottle.WaitAsync(ct);
        try
        {
            await using var conn = OpenOnPrem();
            return await query(conn);
        }
        finally { OnPremThrottle.Release(); }
    }

    // The country connection string's default DB may not be the country's actual
    // DB name (e.g. "bflksa") — dataName is resolved from OnPremBackup first
    // (see WhBoxItemsSource.ResolveDataNameAsync) and forced here via InitialCatalog.
    private SqlConnection OpenCountryWithDataName(string country, string dataName)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
        {
            InitialCatalog = dataName,
            ConnectTimeout = ConnectTimeoutSeconds
        };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    // Splits a [from, to] date range into a historical portion (strictly before
    // today — safe to answer from OnPremBackup) and whether today is included
    // (must be answered from the regional server). Returns null HistFrom/HistTo
    // when the whole range is today or later (nothing historical to fetch).
    private static (DateTime? HistFrom, DateTime? HistTo, bool IncludesToday) SplitDateRange(
        DateTime dateFrom, DateTime dateTo)
    {
        var today = DateTime.Today;
        var from  = dateFrom.Date;
        var to    = dateTo.Date;
        var includesToday = to >= today;
        var histTo = to < today ? to : today.AddDays(-1);
        var hasHistorical = from <= histTo;
        return (hasHistorical ? from : null, hasHistorical ? histTo : null, includesToday);
    }

    // ── Dropdowns ────────────────────────────────────────────────────────────

    /// <summary>
    /// Countries from BFLDATA.dbo.DataSettings on OnPremBackup (SIMCountry exists there).
    /// Filters to countries that have a DataName configured (i.e. a linked DB exists).
    /// </summary>
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPrem();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT SIMCountry
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
               AND SIMCountry NOT IN ('ECOM')
               AND DataName   IS NOT NULL AND LTRIM(RTRIM(DataName))   <> ''
             ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Stores for the country, from OnPremBackup's BFLDATA.dbo.DataSettings
    /// (SIMCountry filter) — same source for every country now, UAE included,
    /// rather than hitting the regional server just to populate a dropdown.
    /// </summary>
    public async Task<List<string>> GetStoresAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPrem();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT ShopName
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry = @country
               AND ShopName   IS NOT NULL AND ShopName <> ''
               AND Concept    <> 'Warehouse'
             ORDER BY ShopName",
            new { country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ── Shop resolution (OnPremBackup) ──────────────────────────────────────

    private record StoreRow(string ShopName, string DataName, string CostCodeTo, string LocCodeTo);

    // One row per shop (never per DataName — a DataName can be shared by several
    // shops, both within UAE and, less commonly, across countries), so each
    // shop's own CostCodeTo/LocCodeTo is known up front rather than re-derived
    // via a loose join later. Works for any country, not just UAE.
    // store: null = every shop in the country.
    private static async Task<List<StoreRow>> GetStoresOnPremAsync(
        SqlConnection onprem, string country, string? store, CancellationToken ct)
    {
        var storeFilter = string.IsNullOrWhiteSpace(store) ? "" : "\n   AND ShopName = @store";
        var sql = $@"
            SELECT ShopName, DataName, CostCodeTo, LocCodeTo
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry = @country
               AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''
               AND Concept <> 'Warehouse'{storeFilter}";
        var rows = await onprem.QueryAsync<StoreRow>(new CommandDefinition(
            sql, new { country, store }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ── Main query ────────────────────────────────────────────────────────────

    /// <summary>
    /// Countries: null/empty means every SIM country ("BFL Group") — each is
    /// queried independently (concurrently) and a per-country failure is
    /// collected as a warning rather than failing the whole request.
    /// </summary>
    public async Task<TransferHistoryResult> GetTransferHistoryAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
    {
        var countries = f.Countries is { Count: > 0 } ? f.Countries : await GetCountriesAsync(ct);
        var warnings  = new List<string>();

        var tasks = countries.Select(async country =>
        {
            try { return await GetForCountryAsync(country, f, ct); }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{country}: {ex.Message}");
                return new List<TransferHistoryRow>();
            }
        });

        var perCountry = await Task.WhenAll(tasks);
        // SrNo is re-numbered here (not trusted from SQL) because each country's
        // ROW_NUMBER() restarts at 1 — fine for a single country, meaningless once
        // multiple countries are merged for "BFL Group".
        var all = perCountry.SelectMany(r => r)
            .OrderBy(r => r.TrfDate).ThenBy(r => r.TrfNo).ThenBy(r => r.GINNo)
            .Select((r, i) => r with { SrNo = i + 1 })
            .ToList();
        return new TransferHistoryResult(all, warnings);
    }

    private async Task<List<TransferHistoryRow>> GetForCountryAsync(
        string country, TransferHistoryFilter f, CancellationToken ct)
    {
        if (country == UaeCountry)
        {
            // UAE has no regional server — always OnPremBackup, whole range at once.
            List<StoreRow> uaeStores;
            await using (var onprem = OpenOnPrem())
            {
                uaeStores = await GetStoresOnPremAsync(onprem, UaeCountry, f.Store, ct);
            }
            return await GetForStoresOnPremAsync(uaeStores, f, f.DateFrom.Date, f.DateTo.Date, isUae: true, ct);
        }

        // Search ignores the date range entirely (matches any date in history) —
        // there's no date boundary to split on, so keep it fully on the regional
        // server, the single authoritative source, exactly like before this change.
        if (!string.IsNullOrWhiteSpace(f.SearchValue))
            return await GetForCountryRegionalAsync(country, f, ct);

        var (histFrom, histTo, includesToday) = SplitDateRange(f.DateFrom, f.DateTo);
        var tasks = new List<Task<List<TransferHistoryRow>>>();

        if (histFrom is not null)
        {
            tasks.Add(Task.Run(async () =>
            {
                List<StoreRow> stores;
                await using (var onprem = OpenOnPrem())
                {
                    stores = await GetStoresOnPremAsync(onprem, country, f.Store, ct);
                }
                return await GetForStoresOnPremAsync(stores, f, histFrom.Value, histTo!.Value, isUae: false, ct);
            }));
        }
        if (includesToday)
        {
            var today = DateTime.Today;
            tasks.Add(GetForCountryRegionalAsync(country, f with { DateFrom = today, DateTo = today }, ct));
        }

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    // One connection per shop, run concurrently — a single SqlConnection can't
    // run concurrent commands, and going one at a time was the main reason a
    // "every store" load took so long: each linked-server round trip against
    // OnPremBackup was paid serially instead of in parallel.
    private async Task<List<TransferHistoryRow>> GetForStoresOnPremAsync(
        List<StoreRow> stores, TransferHistoryFilter f, DateTime from, DateTime to, bool isUae, CancellationToken ct)
    {
        var perStore = await Task.WhenAll(stores.Select(s => WithOnPremAsync(async conn =>
        {
            var sql  = BuildSqlOnPrem(s, f, from, to, isUae, out var parms);
            var rows = await conn.QueryAsync<TransferHistoryRow>(
                new CommandDefinition(sql, parms,
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }, ct)));
        return perStore.SelectMany(r => r).ToList();
    }

    // Non-UAE: connect directly to the country server, but override the default
    // database to the country's actual DB name (e.g. "bflksa"). The connection
    // string may point to a different default DB, so we resolve the real name
    // from OnPremBackup first.
    private async Task<List<TransferHistoryRow>> GetForCountryRegionalAsync(
        string country, TransferHistoryFilter f, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);
        var sql  = BuildSqlCountry(f, out var parms);
        var rows = await conn.QueryAsync<TransferHistoryRow>(
            new CommandDefinition(sql, parms,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ── Summary cards (Transfer Count / Transfer Qty / GIN Count / GIN Qty) ────
    //
    // Two computation paths per level (whole-country and single-store):
    //   - No Without Pallet/GIN/GRN or Search filter active: the fast/default
    //     path — Transfer via a plain vTransferDetail aggregate, GIN via a
    //     direct vGoodsIssueplt EntryDate(+ShopIssue) aggregate. Verified
    //     directly: select count(distinct srno),sum(qty) from
    //     bfldata..vgoodsissueplt where entrydate = '19/09/2026' and
    //     ShopIssue = 'BFLFLAGSHIPDXB'.
    //   - Any of those filters active: derive the SAME "eligible TrfNo" set the
    //     Detailed view itself uses (BuildSqlOnPrem/BuildSqlCountry's own
    //     header+join+filter shape, via AppendCommonFilters below), then
    //     aggregate both Transfer and GIN off that one set — so the cards, the
    //     By Store/By Country breakdown, and the Division/Department/GroupCode/
    //     Brand popup all agree with what the Detailed view actually shows.

    // Plain class with a default constructor (not a positional record) — Dapper's
    // property-setter binding tolerates column-set/order quirks that its
    // constructor-matching path for records does not (observed: UAE summary rows
    // failed with "a parameterless default constructor or one matching signature
    // is required" even though the SQL and record shape looked right).
    private class CountQtyRow
    {
        public int TransferCount { get; set; }
        public int? TransferQty { get; set; }
    }

    private class GinCountQtyRow
    {
        public int GinCount { get; set; }
        public int? GinQty { get; set; }
    }

    private class FilteredSummaryRow
    {
        public int TransferCount { get; set; }
        public int? TransferQty { get; set; }
        public int GinCount { get; set; }
        public int? GinQty { get; set; }
    }

    private static bool HasExtraFilters(TransferHistoryFilter f) =>
        f.WithoutPallet || f.WithoutGin || f.WithoutGrn || !string.IsNullOrWhiteSpace(f.SearchValue);

    /// <summary>
    /// Per-country totals, scoped to Country + date range, PLUS the same
    /// Without Pallet/GIN/GRN and Search filters as the Detailed view when any
    /// are active. f.Countries: null/empty = every SIM country.
    /// </summary>
    public async Task<TransferSummaryResult> GetTransferSummaryAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
    {
        var list = f.Countries is { Count: > 0 } ? f.Countries.ToList() : await GetCountriesAsync(ct);
        var warnings = new List<string>();

        var tasks = list.Select(async country =>
        {
            try { return await GetSummaryForCountryAsync(country, f, ct); }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{country} summary: {ex.Message}");
                return new TransferSummary(country, 0, 0, 0, 0);
            }
        });
        var results = await Task.WhenAll(tasks);
        return new TransferSummaryResult(results.OrderBy(s => s.Country).ToList(), warnings);
    }

    private class WarehouseCode
    {
        public string? CostCodeTo { get; set; }
        public string? LocCodeTo { get; set; }
    }

    // The warehouse's own CostCodeTo/LocCodeTo (the "R1" cost center) — same
    // Concept = 'Warehouse' lookup GetStoresAsync/BuildSqlOnPrem/BuildSqlCountry
    // already use to exclude warehouse rows, just resolved to actual code values
    // here instead of a ShopName NOT IN (...) filter. This differs per country
    // (e.g. KSA is '005'/'05'), so it must be looked up, never hardcoded. Uses
    // the "Country" column (not "SIMCountry") — confirmed directly:
    // select costcodeto, loccodeto from bfldata..datasettings where Country = 'KSA' and concept = 'warehouse'.
    private static async Task<WarehouseCode> ResolveWarehouseCodeAsync(
        SqlConnection conn, string dataSettingsTable, string? country, CancellationToken ct)
    {
        var countryFilter = country is null ? "" : " AND Country = @country";
        var sql = $@"
            SELECT TOP 1 CostCodeTo, LocCodeTo
              FROM {dataSettingsTable} WITH (NOLOCK)
             WHERE Concept = 'Warehouse'{countryFilter}";
        var row = await conn.QuerySingleOrDefaultAsync<WarehouseCode>(new CommandDefinition(
            sql, new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return row ?? new WarehouseCode();
    }

    // dataNameTable e.g. "[EX2KSA]..vTransferDetail" (OnPremBackup linked server) or
    // just "vTransferDetail" (regional: lives in the country's own dataName database,
    // same as transferheader — NOT in the sibling "BFLDATA" db that vGoodsIssue/
    // vGoodsIssueplt/DataSettings live in; confirmed via a direct query against
    // bflksa..vTransferDetail).
    // whCostCodeTo/whLocCodeTo exclude the warehouse's own internal transfers —
    // null (no DataSettings Warehouse row found) means don't filter.
    private static string TransferSummarySql(string transferDetailTable) => $@"
        SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
          FROM {transferDetailTable} WITH (NOLOCK)
         WHERE TrfDate >= @from AND TrfDate <= @to
           AND (@whCostCodeTo IS NULL OR CostCodeTo <> @whCostCodeTo)
           AND (@whLocCodeTo  IS NULL OR LocCodeTo  <> @whLocCodeTo)";

    private async Task<TransferSummary> GetSummaryForCountryAsync(
        string country, TransferHistoryFilter f, CancellationToken ct)
    {
        if (country == UaeCountry)
            return await GetCountrySummaryOnPremAsync(country, f, f.DateFrom.Date, f.DateTo.Date, ct);

        // Search ignores the date range entirely (matches any date in history) —
        // same bypass GetForCountryAsync applies to the Detailed view's own query.
        if (!string.IsNullOrWhiteSpace(f.SearchValue))
            return await GetCountrySummaryRegionalAsync(country, f, f.DateFrom.Date, f.DateTo.Date, ct);

        var (histFrom, histTo, includesToday) = SplitDateRange(f.DateFrom, f.DateTo);
        var histTask = histFrom is not null
            ? GetCountrySummaryOnPremAsync(country, f, histFrom.Value, histTo!.Value, ct)
            : Task.FromResult(new TransferSummary(country, 0, 0, 0, 0));
        var todayTask = includesToday
            ? GetCountrySummaryRegionalAsync(country, f, DateTime.Today, DateTime.Today, ct)
            : Task.FromResult(new TransferSummary(country, 0, 0, 0, 0));

        await Task.WhenAll(histTask, todayTask);
        var h = histTask.Result; var t = todayTask.Result;
        return new TransferSummary(country,
            h.TransferCount + t.TransferCount, h.TransferQty + t.TransferQty,
            h.GinCount + t.GinCount, h.GinQty + t.GinQty);
    }

    // OnPremBackup path: loops DISTINCT DataName (not per-shop — TransferSummarySql
    // already aggregates every shop sharing that DataName in one query, only
    // excluding the warehouse's own codes, so per-shop looping would just be
    // redundant extra round trips for the same total).
    private async Task<TransferSummary> GetCountrySummaryOnPremAsync(
        string country, TransferHistoryFilter f, DateTime from, DateTime to, CancellationToken ct)
    {
        List<string> dataNames;
        await using (var onprem = OpenOnPrem())
        {
            dataNames = (await onprem.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT DataName
                  FROM BFLDATA.dbo.DataSettings
                 WHERE SIMCountry = @country
                   AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        var wh = await WithOnPremAsync(conn =>
            ResolveWarehouseCodeAsync(conn, "BFLDATA.dbo.DataSettings", country, ct), ct);
        var extraFilters = HasExtraFilters(f);

        // GIN is scoped directly by its own EntryDate — no shop filter at this
        // whole-country level (every shop sharing this DataName), no TrfNo
        // correlation to transferheader needed at all. Confirmed via direct
        // query: select count(distinct srno),sum(qty) from bfldata..vgoodsissueplt
        // where entrydate = '19/09/2026' and ShopIssue = 'BFLFLAGSHIPDXB'.
        // (Only when no Without Pallet/GIN/GRN/Search filter is active — see
        // FilteredSummarySql below for when one is.)
        var perDataNameTask = Task.WhenAll(dataNames.Select(dn =>
        {
            // GIN lives in the central BFLDATA.dbo db for UAE, but in each
            // country's OWN DataName db for everyone else — same "wrong db"
            // mistake already caught and fixed for vTransferDetail, just
            // missed here too. Confirmed directly: bflksa..vGoodsIssuePlt,
            // not BFLDATA.dbo.vGoodsIssueplt, for KSA.
            var ginTable   = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{dn}]..vGoodsIssueplt";
            var buildTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssue"    : $"[{dn}]..vGoodsIssue";
            var transferDetailTable = $"[{dn}]..vTransferDetail";

            return WithOnPremAsync(async conn =>
            {
                CountQtyRow transferRow;
                GinCountQtyRow gin;
                if (extraFilters)
                {
                    var cte = BuildEligibleTrfNoCte(
                        $"[{dn}]..transferheader", buildTable, ginTable, $"[{dn}]..GRNHeaderRF", f, from, to,
                        costCodeTo: null, locCodeTo: null, whCostCodeTo: wh.CostCodeTo, whLocCodeTo: wh.LocCodeTo,
                        requireGin: false, out var fp);
                    var row = await conn.QuerySingleAsync<FilteredSummaryRow>(new CommandDefinition(
                        cte + FilteredSummarySql(transferDetailTable),
                        fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    transferRow = new CountQtyRow { TransferCount = row.TransferCount, TransferQty = row.TransferQty };
                    gin = new GinCountQtyRow { GinCount = row.GinCount, GinQty = row.GinQty };
                }
                else
                {
                    var toEnd = to.AddDays(1).AddSeconds(-1);
                    transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                        TransferSummarySql(transferDetailTable),
                        new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                    gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition($@"
                        SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
                          FROM {ginTable} WITH (NOLOCK)
                         WHERE EntryDate >= @from AND EntryDate <= @to",
                        new { from, to = toEnd }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                }
                return (transferRow, gin);
            }, ct);
        }));

        await perDataNameTask;
        var transferCount = perDataNameTask.Result.Sum(r => r.transferRow.TransferCount);
        var transferQty   = perDataNameTask.Result.Sum(r => r.transferRow.TransferQty ?? 0);
        var ginCount      = perDataNameTask.Result.Sum(r => r.gin.GinCount);
        var ginQty        = perDataNameTask.Result.Sum(r => r.gin.GinQty ?? 0);

        return new TransferSummary(country, transferCount, transferQty, ginCount, ginQty);
    }

    // Regional server path (non-UAE only) — today's slice, or the whole history
    // when Search bypasses the historical/today split entirely.
    private async Task<TransferSummary> GetCountrySummaryRegionalAsync(
        string country, TransferHistoryFilter f, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);
        var wh = await ResolveWarehouseCodeAsync(conn, "BFLDATA..DataSettings", country: null, ct);

        if (HasExtraFilters(f))
        {
            var cte = BuildEligibleTrfNoCte(
                "transferheader", "vGoodsIssue", "vgoodsissueplt", "GRNHeaderRF", f, from, to,
                costCodeTo: null, locCodeTo: null, whCostCodeTo: wh.CostCodeTo, whLocCodeTo: wh.LocCodeTo,
                requireGin: false, out var fp);
            var row = await conn.QuerySingleAsync<FilteredSummaryRow>(new CommandDefinition(
                cte + FilteredSummarySql("vTransferDetail"),
                fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return new TransferSummary(country, row.TransferCount, row.TransferQty ?? 0, row.GinCount, row.GinQty ?? 0);
        }

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
            TransferSummarySql("vTransferDetail"),
            new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // vGoodsIssueplt lives in the country's own dataName db here (same as
        // transferheader/vTransferDetail), NOT the sibling BFLDATA db —
        // confirmed directly: bflksa..vGoodsIssuePlt, not BFLDATA..vgoodsissueplt.
        // Scoped by its own EntryDate — no shop filter at this whole-country level.
        var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
            SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
              FROM vgoodsissueplt WITH (NOLOCK)
             WHERE EntryDate >= @from AND EntryDate <= @to",
            new { from, to = toEnd }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new TransferSummary(country, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
    }

    // ── Store-level summary (single store + all stores in a country) ──────────

    private static string StoreTransferSummarySql(string transferDetailTable) => $@"
        SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
          FROM {transferDetailTable} WITH (NOLOCK)
         WHERE TrfDate >= @from AND TrfDate <= @to
           AND CostCodeTo = @costCodeTo AND LocCodeTo = @locCodeTo";

    // from/to: date-only: this does the end-of-day adjustment itself before
    // delegating, so every caller can just pass plain dates.
    //
    // No Without Pallet/GIN/GRN/Search filter active: GIN is scoped directly by
    // its own EntryDate + ShopIssue — confirmed via direct query: select
    // count(distinct srno),sum(qty) from bfldata..vgoodsissueplt where
    // entrydate = '19/09/2026' and ShopIssue = 'BFLFLAGSHIPDXB'. Simpler and
    // cheaper than correlating via transferheader's TrfNo/CostCodeTo/LocCodeTo.
    // Any of those filters active: falls back to the Eligible-TrfNo-set
    // approach (FilteredSummarySql) so this matches the Detailed view exactly.
    private static async Task<TransferSummary> GetOneStoreSummaryAsync(
        SqlConnection conn, TransferHistoryFilter f, string shopName,
        string transferHeaderTable, string transferDetailTable, string buildTable, string ginTable, string grnTable,
        string costCodeTo, string locCodeTo, DateTime from, DateTime to, CancellationToken ct)
    {
        if (HasExtraFilters(f))
        {
            var cte = BuildEligibleTrfNoCte(
                transferHeaderTable, buildTable, ginTable, grnTable, f, from, to,
                costCodeTo, locCodeTo, whCostCodeTo: null, whLocCodeTo: null, requireGin: false, out var fp);
            var row = await conn.QuerySingleAsync<FilteredSummaryRow>(new CommandDefinition(
                cte + FilteredSummarySql(transferDetailTable),
                fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return new TransferSummary(shopName, row.TransferCount, row.TransferQty ?? 0, row.GinCount, row.GinQty ?? 0);
        }

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
            StoreTransferSummarySql(transferDetailTable),
            new { from, to = toEnd, costCodeTo, locCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition($@"
            SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
              FROM {ginTable} WITH (NOLOCK)
             WHERE EntryDate >= @from AND EntryDate <= @to
               AND ShopIssue = @shopName",
            new { shopName, from, to = toEnd }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new TransferSummary(shopName, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
    }

    // GIN lives centrally (BFLDATA.dbo) for UAE, but in the shop's own
    // DataName db for everyone else — same "wrong db" mistake already fixed
    // for vTransferDetail, just missed here too (confirmed: bflksa..vGoodsIssuePlt).
    private Task<TransferSummary> GetOneStoreSummaryOnPremAsync(
        string country, TransferHistoryFilter f, StoreRow s, DateTime from, DateTime to, CancellationToken ct)
    {
        var ginTable   = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{s.DataName}]..vGoodsIssueplt";
        var buildTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssue"    : $"[{s.DataName}]..vGoodsIssue";
        return WithOnPremAsync(conn => GetOneStoreSummaryAsync(
            conn, f, s.ShopName, $"[{s.DataName}]..transferheader", $"[{s.DataName}]..vTransferDetail",
            buildTable, ginTable, $"[{s.DataName}]..GRNHeaderRF",
            s.CostCodeTo, s.LocCodeTo, from, to, ct), ct);
    }

    // Regional (today-only, or the whole history when Search bypasses the
    // split) — reuses the shop's CostCodeTo/LocCodeTo already resolved from
    // OnPremBackup's DataSettings rather than re-querying the regional server
    // for it: DataSettings is reference/master data, not subject to the same
    // same-day sync lag that transactional tables are. vGoodsIssueplt lives in
    // the country's own dataName db here too, not the sibling BFLDATA db.
    private async Task<TransferSummary> GetOneStoreSummaryRegionalAsync(
        string country, TransferHistoryFilter f, string dataName, StoreRow s, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var conn = OpenCountryWithDataName(country, dataName);
        return await GetOneStoreSummaryAsync(
            conn, f, s.ShopName, "transferheader", "vTransferDetail", "vGoodsIssue", "vgoodsissueplt", "GRNHeaderRF",
            s.CostCodeTo, s.LocCodeTo, from, to, ct);
    }

    /// <summary>
    /// Same metrics as GetTransferSummaryAsync but scoped to one specific store
    /// (its own CostCodeTo/LocCodeTo, same precision fix as the detail query) —
    /// shown as an extra card alongside the country card when a store is picked.
    /// f.Countries must be exactly one country and f.Store must be set.
    /// Returns null if the store can't be resolved in DataSettings.
    /// </summary>
    public async Task<TransferSummary?> GetStoreSummaryAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
    {
        var country = f.Countries?.SingleOrDefault();
        var store   = f.Store;
        if (string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(store)) return null;

        var from = f.DateFrom.Date;
        var to   = f.DateTo.Date;

        StoreRow? s;
        await using (var onprem = OpenOnPrem())
        {
            s = (await GetStoresOnPremAsync(onprem, country, store, ct)).SingleOrDefault();
        }
        if (s is null) return null;

        if (country == UaeCountry)
            return await GetOneStoreSummaryOnPremAsync(country, f, s, from, to, ct);

        if (!string.IsNullOrWhiteSpace(f.SearchValue))
            return await GetTodayStoreSummaryRegionalAsync(country, f, s, ct);

        var (histFrom, histTo, includesToday) = SplitDateRange(from, to);
        var histTask = histFrom is not null
            ? GetOneStoreSummaryOnPremAsync(country, f, s, histFrom.Value, histTo!.Value, ct)
            : Task.FromResult(new TransferSummary(store, 0, 0, 0, 0));
        var todayTask = includesToday
            ? GetTodayStoreSummaryRegionalAsync(country, f, s, ct)
            : Task.FromResult(new TransferSummary(store, 0, 0, 0, 0));

        await Task.WhenAll(histTask, todayTask);
        var h = histTask.Result; var t = todayTask.Result;
        return new TransferSummary(store,
            h.TransferCount + t.TransferCount, h.TransferQty + t.TransferQty,
            h.GinCount + t.GinCount, h.GinQty + t.GinQty);
    }

    // "Today" store summary — also reused for the Search-bypasses-the-split case
    // (the actual from/to don't matter then; BuildEligibleTrfNoCte drops the
    // date filter entirely when Search is active, same as BuildSqlOnPrem/
    // BuildSqlCountry).
    private async Task<TransferSummary> GetTodayStoreSummaryRegionalAsync(
        string country, TransferHistoryFilter f, StoreRow s, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        var today = DateTime.Today;
        return await GetOneStoreSummaryRegionalAsync(country, f, dataName, s, today, today, ct);
    }

    /// <summary>
    /// Per-STORE breakdown within a single country — same shape as
    /// GetTransferSummaryAsync's per-country breakdown, just one level down.
    /// Shown below the total cards when a single country is selected with
    /// "(All stores)". f.Countries must be exactly one country; f.Store is
    /// ignored (every store in the country is included).
    /// </summary>
    public async Task<List<TransferSummary>> GetStoreSummariesAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
    {
        var country = f.Countries!.Single();
        var from = f.DateFrom.Date;
        var to   = f.DateTo.Date;

        List<StoreRow> stores;
        await using (var onprem = OpenOnPrem())
        {
            stores = await GetStoresOnPremAsync(onprem, country, store: null, ct);
        }

        if (country == UaeCountry)
        {
            var tasks = stores.Select(s => GetOneStoreSummaryOnPremAsync(country, f, s, from, to, ct));
            var results = await Task.WhenAll(tasks);
            return results.OrderBy(r => r.Country).ToList();
        }

        if (!string.IsNullOrWhiteSpace(f.SearchValue))
        {
            await using var onprem = OpenOnPrem();
            var searchDataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
            if (string.IsNullOrWhiteSpace(searchDataName))
                throw new InvalidOperationException(
                    $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

            var searchTasks = stores.Select(s => GetOneStoreSummaryRegionalAsync(country, f, searchDataName, s, from, to, ct));
            var searchResults = await Task.WhenAll(searchTasks);
            return searchResults.OrderBy(r => r.Country).ToList();
        }

        var (histFrom, histTo, includesToday) = SplitDateRange(from, to);

        var histTasks = histFrom is not null
            ? stores.Select(s => GetOneStoreSummaryOnPremAsync(country, f, s, histFrom.Value, histTo!.Value, ct)).ToArray()
            : [];

        Task<TransferSummary>[] todayTasks = [];
        if (includesToday)
        {
            await using var onprem = OpenOnPrem();
            var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
            if (string.IsNullOrWhiteSpace(dataName))
                throw new InvalidOperationException(
                    $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

            var today = DateTime.Today;
            todayTasks = stores.Select(s => GetOneStoreSummaryRegionalAsync(country, f, dataName, s, today, today, ct)).ToArray();
        }

        await Task.WhenAll(histTasks.Concat(todayTasks));

        var byShop = new Dictionary<string, (int TransferCount, int TransferQty, int GinCount, int GinQty)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in histTasks.Select(x => x.Result).Concat(todayTasks.Select(x => x.Result)))
        {
            var cur = byShop.GetValueOrDefault(t.Country);
            byShop[t.Country] = (
                cur.TransferCount + t.TransferCount, cur.TransferQty + t.TransferQty,
                cur.GinCount + t.GinCount, cur.GinQty + t.GinQty);
        }
        return byShop
            .Select(kv => new TransferSummary(kv.Key, kv.Value.TransferCount, kv.Value.TransferQty, kv.Value.GinCount, kv.Value.GinQty))
            .OrderBy(r => r.Country).ToList();
    }

    // ── Division/Department/GroupCode/Brand drill-down ─────────────────────────
    //
    // Shown as a popup when a summary stat card is clicked: Transfer Count/Qty
    // open the "based on transfers" breakdown, GIN Count/Qty open the "based on
    // GIN" breakdown. Both are grouped by vTransferDetail.groupcode — the only
    // place item-line GroupCode lives for this report — with Division/Department/
    // Brand resolved afterward via one extra usa.dbo.USAPriority lookup.
    //
    // usa.dbo.USAPriority only exists on OnPremBackup (every existing join to it
    // in this codebase runs there, never against a country's own regional
    // server), so today's regional-only slice can't join to it directly. Instead
    // every source — OnPrem historical AND regional today — returns plain
    // (GroupCode, Count, Qty) rows; GroupCode -> Division/Department/Brand is
    // resolved in ONE extra OnPremBackup round trip for the merged, deduplicated
    // set of codes actually seen, then joined in C#.

    private class GroupCodeChunkRow
    {
        public string? GroupCode { get; set; }
        public int Qty { get; set; }
    }

    private class PriorityRow
    {
        public string? GroupCode  { get; set; }
        public string? Division   { get; set; }
        public string? Department { get; set; }
        public string? Brand      { get; set; }
    }

    public Task<TransferGinBreakdownResult> GetTransferBreakdownAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
        => GetBreakdownAsync(f, byGin: false, ct);

    public Task<TransferGinBreakdownResult> GetGinBreakdownAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
        => GetBreakdownAsync(f, byGin: true, ct);

    private async Task<TransferGinBreakdownResult> GetBreakdownAsync(
        TransferHistoryFilter f, bool byGin, CancellationToken ct)
    {
        var list = f.Countries is { Count: > 0 } ? f.Countries.ToList() : await GetCountriesAsync(ct);
        var warnings = new List<string>();

        var tasks = list.Select(async country =>
        {
            try { return await GetBreakdownChunksForCountryAsync(country, f, byGin, ct); }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{country}: {ex.Message}");
                return new List<GroupCodeChunkRow>();
            }
        });
        var perCountry = await Task.WhenAll(tasks);
        return await FinalizeBreakdownAsync(perCountry.SelectMany(r => r).ToList(), warnings, ct);
    }

    // Mirrors GetStoreSummaryAsync (a specific store) / GetSummaryForCountryAsync
    // (whole country, warehouse-excluded) — same historical/today/Search-bypass
    // routing, just returning per-GroupCode chunks instead of one aggregate.
    private async Task<List<GroupCodeChunkRow>> GetBreakdownChunksForCountryAsync(
        string country, TransferHistoryFilter f, bool byGin, CancellationToken ct)
    {
        var from = f.DateFrom.Date;
        var to   = f.DateTo.Date;

        if (!string.IsNullOrWhiteSpace(f.Store))
        {
            StoreRow? s;
            await using (var onprem = OpenOnPrem())
            {
                s = (await GetStoresOnPremAsync(onprem, country, f.Store, ct)).SingleOrDefault();
            }
            if (s is null) return [];

            if (country == UaeCountry)
                return await GetOneStoreBreakdownChunksOnPremAsync(country, f, s, from, to, byGin, ct);

            if (!string.IsNullOrWhiteSpace(f.SearchValue))
                return await GetOneStoreBreakdownChunksRegionalAsync(country, f, s, from, to, byGin, ct);

            var (histFrom, histTo, includesToday) = SplitDateRange(from, to);
            var histTask = histFrom is not null
                ? GetOneStoreBreakdownChunksOnPremAsync(country, f, s, histFrom.Value, histTo!.Value, byGin, ct)
                : Task.FromResult(new List<GroupCodeChunkRow>());
            var todayTask = includesToday
                ? GetOneStoreBreakdownChunksRegionalAsync(country, f, s, DateTime.Today, DateTime.Today, byGin, ct)
                : Task.FromResult(new List<GroupCodeChunkRow>());

            await Task.WhenAll(histTask, todayTask);
            return histTask.Result.Concat(todayTask.Result).ToList();
        }

        if (country == UaeCountry)
            return await GetCountryBreakdownChunksOnPremAsync(country, f, from, to, byGin, ct);

        if (!string.IsNullOrWhiteSpace(f.SearchValue))
            return await GetCountryBreakdownChunksRegionalAsync(country, f, from, to, byGin, ct);

        var (chHistFrom, chHistTo, chIncludesToday) = SplitDateRange(from, to);
        var chHistTask = chHistFrom is not null
            ? GetCountryBreakdownChunksOnPremAsync(country, f, chHistFrom.Value, chHistTo!.Value, byGin, ct)
            : Task.FromResult(new List<GroupCodeChunkRow>());
        var chTodayTask = chIncludesToday
            ? GetCountryBreakdownChunksRegionalAsync(country, f, DateTime.Today, DateTime.Today, byGin, ct)
            : Task.FromResult(new List<GroupCodeChunkRow>());

        await Task.WhenAll(chHistTask, chTodayTask);
        return chHistTask.Result.Concat(chTodayTask.Result).ToList();
    }

    // requireGin (via BuildEligibleTrfNoCte, only applied when a filter is
    // active) restricts the GIN breakdown's eligible set to transfers that also
    // have a linked GIN — otherwise a transfer matching e.g. Without GRN alone
    // (but with no GIN at all) would wrongly contribute its item quantity to
    // the GIN breakdown despite contributing 0 to the GIN Qty card.
    private Task<List<GroupCodeChunkRow>> GetOneStoreBreakdownChunksOnPremAsync(
        string country, TransferHistoryFilter f, StoreRow s, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        var ginTable   = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{s.DataName}]..vGoodsIssueplt";
        var buildTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssue"    : $"[{s.DataName}]..vGoodsIssue";
        var transferDetailTable = $"[{s.DataName}]..vTransferDetail";

        if (HasExtraFilters(f))
        {
            return WithOnPremAsync(async conn =>
            {
                var cte = BuildEligibleTrfNoCte(
                    $"[{s.DataName}]..transferheader", buildTable, ginTable, $"[{s.DataName}]..GRNHeaderRF", f, from, to,
                    s.CostCodeTo, s.LocCodeTo, whCostCodeTo: null, whLocCodeTo: null, requireGin: byGin, out var fp);
                var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                    cte + FilteredGroupCodeSql(transferDetailTable),
                    fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return rows.AsList();
            }, ct);
        }

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var sql = byGin
            ? StoreGinGroupCodeSql(transferDetailTable, ginTable)
            : StoreTransferGroupCodeSql(transferDetailTable);
        return WithOnPremAsync(async conn =>
        {
            var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                sql, new { from, to = toEnd, costCodeTo = s.CostCodeTo, locCodeTo = s.LocCodeTo, shopName = s.ShopName },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }, ct);
    }

    private async Task<List<GroupCodeChunkRow>> GetOneStoreBreakdownChunksRegionalAsync(
        string country, TransferHistoryFilter f, StoreRow s, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);

        if (HasExtraFilters(f))
        {
            var cte = BuildEligibleTrfNoCte(
                "transferheader", "vGoodsIssue", "vgoodsissueplt", "GRNHeaderRF", f, from, to,
                s.CostCodeTo, s.LocCodeTo, whCostCodeTo: null, whLocCodeTo: null, requireGin: byGin, out var fp);
            var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                cte + FilteredGroupCodeSql("vTransferDetail"),
                fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var sql = byGin
            ? StoreGinGroupCodeSql("vTransferDetail", "vgoodsissueplt")
            : StoreTransferGroupCodeSql("vTransferDetail");
        var rows2 = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
            sql, new { from, to = toEnd, costCodeTo = s.CostCodeTo, locCodeTo = s.LocCodeTo, shopName = s.ShopName },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows2.AsList();
    }

    // Loops DISTINCT DataName the same way GetCountrySummaryOnPremAsync does —
    // warehouse-excluded, not scoped to any one shop.
    private async Task<List<GroupCodeChunkRow>> GetCountryBreakdownChunksOnPremAsync(
        string country, TransferHistoryFilter f, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        List<string> dataNames;
        await using (var onprem = OpenOnPrem())
        {
            dataNames = (await onprem.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT DataName
                  FROM BFLDATA.dbo.DataSettings
                 WHERE SIMCountry = @country
                   AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        var wh = await WithOnPremAsync(conn =>
            ResolveWarehouseCodeAsync(conn, "BFLDATA.dbo.DataSettings", country, ct), ct);
        var extraFilters = HasExtraFilters(f);

        var perDataNameTask = Task.WhenAll(dataNames.Select(dn =>
        {
            var ginTable   = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{dn}]..vGoodsIssueplt";
            var buildTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssue"    : $"[{dn}]..vGoodsIssue";
            var transferDetailTable = $"[{dn}]..vTransferDetail";

            if (extraFilters)
            {
                return WithOnPremAsync(async conn =>
                {
                    var cte = BuildEligibleTrfNoCte(
                        $"[{dn}]..transferheader", buildTable, ginTable, $"[{dn}]..GRNHeaderRF", f, from, to,
                        costCodeTo: null, locCodeTo: null, whCostCodeTo: wh.CostCodeTo, whLocCodeTo: wh.LocCodeTo,
                        requireGin: byGin, out var fp);
                    var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                        cte + FilteredGroupCodeSql(transferDetailTable),
                        fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    return rows.AsList();
                }, ct);
            }

            var sql = byGin
                ? GinGroupCodeSql(transferDetailTable, ginTable)
                : TransferGroupCodeSql(transferDetailTable);
            return WithOnPremAsync(async conn =>
            {
                var toEnd = to.AddDays(1).AddSeconds(-1);
                var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                    sql, new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return rows.AsList();
            }, ct);
        }));

        var perDataName = await perDataNameTask;
        return perDataName.SelectMany(r => r).ToList();
    }

    private async Task<List<GroupCodeChunkRow>> GetCountryBreakdownChunksRegionalAsync(
        string country, TransferHistoryFilter f, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);
        var wh = await ResolveWarehouseCodeAsync(conn, "BFLDATA..DataSettings", country: null, ct);

        if (HasExtraFilters(f))
        {
            var cte = BuildEligibleTrfNoCte(
                "transferheader", "vGoodsIssue", "vgoodsissueplt", "GRNHeaderRF", f, from, to,
                costCodeTo: null, locCodeTo: null, whCostCodeTo: wh.CostCodeTo, whLocCodeTo: wh.LocCodeTo,
                requireGin: byGin, out var fp);
            var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                cte + FilteredGroupCodeSql("vTransferDetail"),
                fp, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var sql = byGin
            ? GinGroupCodeSql("vTransferDetail", "vgoodsissueplt")
            : TransferGroupCodeSql("vTransferDetail");
        var rows2 = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
            sql, new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows2.AsList();
    }

    private static string TransferGroupCodeSql(string transferDetailTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfDate >= @from AND vtd.TrfDate <= @to
           AND (@whCostCodeTo IS NULL OR vtd.CostCodeTo <> @whCostCodeTo)
           AND (@whLocCodeTo  IS NULL OR vtd.LocCodeTo  <> @whLocCodeTo)
         GROUP BY vtd.groupcode";

    private static string StoreTransferGroupCodeSql(string transferDetailTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfDate >= @from AND vtd.TrfDate <= @to
           AND vtd.CostCodeTo = @costCodeTo AND vtd.LocCodeTo = @locCodeTo
         GROUP BY vtd.groupcode";

    // GIN has no line-item GroupCode of its own — the TrfNo set is now derived
    // from the GIN's own EntryDate (+ ShopIssue for the store-scoped variant),
    // matching GetCountrySummaryOnPremAsync/GetOneStoreSummaryAsync's GIN
    // Count/Qty definition — then rolled up via that transfer's own
    // vTransferDetail lines (still an approximation, since a transfer's total
    // Quantity need not exactly equal what was actually GIN'd — same
    // approximation ShipmentStatusService's GIN-flow Division rollup already
    // uses; keeping the TrfNo set aligned with the stat cards at least keeps
    // this breakdown's Grand Total consistent with the GIN Count/Qty cards'
    // scope of transfers).
    private static string GinGroupCodeSql(string transferDetailTable, string ginTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfNo IN (
             SELECT DISTINCT TrfNo FROM {ginTable} WITH (NOLOCK)
              WHERE EntryDate >= @from AND EntryDate <= @to
         )
         GROUP BY vtd.groupcode";

    private static string StoreGinGroupCodeSql(string transferDetailTable, string ginTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfNo IN (
             SELECT DISTINCT TrfNo FROM {ginTable} WITH (NOLOCK)
              WHERE EntryDate >= @from AND EntryDate <= @to
                AND ShopIssue = @shopName
         )
         GROUP BY vtd.groupcode";

    private async Task<TransferGinBreakdownResult> FinalizeBreakdownAsync(
        List<GroupCodeChunkRow> chunks, List<string> warnings, CancellationToken ct)
    {
        var byGroupCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chunks)
        {
            var key = string.IsNullOrWhiteSpace(c.GroupCode) ? "" : c.GroupCode!.Trim();
            byGroupCode[key] = byGroupCode.GetValueOrDefault(key) + c.Qty;
        }

        var groupCodes = byGroupCode.Keys.Where(k => k != "").ToList();
        var priorityByCode = new Dictionary<string, PriorityRow>(StringComparer.OrdinalIgnoreCase);
        if (groupCodes.Count > 0)
        {
            var rows = await WithOnPremAsync(async conn =>
            {
                var sql = @"
                    SELECT groupCode AS GroupCode, DivisionY AS Division, Department, Brand
                      FROM usa.dbo.USAPriority WITH (NOLOCK)
                     WHERE groupCode IN @codes";
                var r = await conn.QueryAsync<PriorityRow>(new CommandDefinition(
                    sql, new { codes = groupCodes }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return r.AsList();
            }, ct);
            foreach (var r in rows)
                if (!string.IsNullOrWhiteSpace(r.GroupCode))
                    priorityByCode[r.GroupCode!.Trim()] = r;
        }

        var merged = new Dictionary<(string Division, string Department, string GroupCode, string Brand), int>();
        foreach (var (code, qty) in byGroupCode)
        {
            var p = code != "" && priorityByCode.TryGetValue(code, out var pr) ? pr : null;
            var key = (Blank(p?.Division), Blank(p?.Department), code == "" ? "(blank)" : code, Blank(p?.Brand));
            merged[key] = merged.GetValueOrDefault(key) + qty;
        }

        var outRows = merged
            .Select(kv => new TransferGinBreakdownRow(kv.Key.Division, kv.Key.Department, kv.Key.GroupCode, kv.Key.Brand, kv.Value))
            .OrderBy(r => r.Division, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Department, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.GroupCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Brand, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TransferGinBreakdownResult(outRows, outRows.Sum(r => r.Qty), warnings);
    }

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "(blank)" : s!.Trim();

    // ── SQL builders ─────────────────────────────────────────────────────────

    // Shop's own CostCodeTo/LocCodeTo (resolved via GetStoresOnPremAsync) scope
    // the query directly — no join back to DataSettings, so a transfer can't
    // leak in just because its CostCodeTo coincidentally matches some OTHER
    // shop sharing this DataName. Works via OnPremBackup's linked-server access
    // for ANY country (UAE, or the historical portion of a non-UAE country).
    // from/to: date-only — end-of-day adjustment happens here, matching how the
    // detail table's own filter dates already need @from/@to bound this way.
    private static string BuildSqlOnPrem(
        StoreRow s, TransferHistoryFilter f, DateTime from, DateTime to, bool isUae, out DynamicParameters p)
    {
        p = new DynamicParameters();
        p.Add("@shopName",   s.ShopName);
        p.Add("@costCodeTo", s.CostCodeTo);
        p.Add("@locCodeTo",  s.LocCodeTo);
        var hasSearch    = !string.IsNullOrWhiteSpace(f.SearchValue);
        var dateFilter   = hasSearch ? "" : "\n   AND a.TrfDate >= @from AND a.TrfDate <= @to";
        if (!hasSearch) { p.Add("@from", from); p.Add("@to", to.AddDays(1).AddSeconds(-1)); }

        // GIN lives centrally (BFLDATA.dbo) for UAE, but in the shop's own
        // DataName db for everyone else — confirmed: bflksa..vGoodsIssuePlt,
        // not BFLDATA.dbo.vGoodsIssueplt, for KSA. Same for vGoodsIssue
        // (Build/Pallet) — confirmed by PalletNo/BuildDate coming back blank
        // for KSA while still reading from the central BFLDATA.dbo table.
        var ginTable   = isUae ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{s.DataName}]..vGoodsIssueplt";
        var buildTable = isUae ? "BFLDATA.dbo.vGoodsIssue"    : $"[{s.DataName}]..vGoodsIssue";

        // PalletNo and BuildDate come from the SAME resolved row (OUTER APPLY,
        // filtered by EntryDate >= a.TrfDate before picking TOP 1 ORDER BY
        // PalletNo DESC) — previously they were two independent lookups: a
        // scalar subquery for PalletNo (correctly filtered by EntryDate first)
        // and a separately-joined windowed subquery for BuildDate (ROW_NUMBER()
        // computed over ALL rows for the TrfNo, date filter applied only
        // afterward on the join). When the globally-highest-PalletNo row for a
        // TrfNo didn't itself satisfy EntryDate >= a.TrfDate, the join found no
        // match and BuildDate came back blank even though PalletNo (via its own,
        // correctly-ordered filter) still showed a value — observed directly:
        // several rows sharing PalletNo DXB/20651 had BuildDate blank while
        // others with the same PalletNo had it populated.
        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfDate, a.TrfNo, c.SrNo) SrNo,
       @shopName ShopName,
       a.TrfNo,
       a.TrfDate,
       Quantity  = CAST(ISNULL((SELECT SUM(Quantity) FROM [{s.DataName}]..vTransferDetail WHERE TrfNo = a.TrfNo), 0) AS INT),
       b.PalletNo,
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM [{s.DataName}]..transferheader           a
  OUTER APPLY (
      SELECT TOP 1 PalletNo, EntryDate
        FROM {buildTable}
       WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate
       ORDER BY PalletNo DESC
  )                                            b
  LEFT JOIN {ginTable}                         c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..GRNHeaderRF          d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..TransferReverse      f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND a.CostCodeTo = @costCodeTo AND a.LocCodeTo = @locCodeTo");

        AppendCommonFilters(sb, p, f, buildTable, includeStoreFilter: false);
        sb.Append("\n ORDER BY a.TrfDate, a.TrfNo");
        return sb.ToString();
    }

    // Country-server path (non-UAE, today's slice only): all tables are local —
    // no linked-server prefix. Kept as the original loose CostCodeTo-only join
    // (not the per-shop precision fix applied to BuildSqlOnPrem) — fixing that
    // here is a separate concern from this date-source split.
    private static string BuildSqlCountry(TransferHistoryFilter f, out DynamicParameters p)
    {
        p = new DynamicParameters();
        var hasSearch  = !string.IsNullOrWhiteSpace(f.SearchValue);
        var dateFilter = hasSearch ? "" : "\n   AND a.TrfDate >= @from AND a.TrfDate <= @to";
        if (!hasSearch) { p.Add("@from", f.DateFrom.Date); p.Add("@to", f.DateTo.Date.AddDays(1).AddSeconds(-1)); }

        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfNo, c.SrNo) SrNo,
       e.ShopName,
       a.TrfNo,
       a.TrfDate,
       Quantity  = CAST(ISNULL((SELECT SUM(Quantity) FROM vTransferDetail WHERE TrfNo = a.TrfNo), 0) AS INT),
       b.PalletNo,
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM transferheader              a
  OUTER APPLY (
      SELECT TOP 1 PalletNo, EntryDate
        FROM vGoodsIssue
       WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate
       ORDER BY PalletNo DESC
  )                                b
  LEFT JOIN vGoodsIssueplt          c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN GRNHeaderRF             d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  JOIN  BFLDATA..DataSettings       e  ON a.CostCodeTo = e.CostCodeTo
  LEFT JOIN TransferReverse         f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND e.ShopName NOT IN (
       SELECT ShopName FROM BFLDATA..DataSettings WHERE Concept = 'Warehouse'
   )");

        AppendCommonFilters(sb, p, f, "vGoodsIssue");
        sb.Append("\n ORDER BY a.TrfDate, a.TrfNo");
        return sb.ToString();
    }

    // includeStoreFilter: false for the OnPremBackup path, where the query is
    // already scoped to one shop's own CostCodeTo/LocCodeTo (see BuildSqlOnPrem)
    // — there's no "e" (DataSettings) alias left to filter on there.
    // buildTable: the SAME vGoodsIssue reference the caller's own query already
    // uses (BFLDATA.dbo.vGoodsIssue / [DataName]..vGoodsIssue / vGoodsIssue) —
    // this was hardcoded to "BFLDATA..vGoodsIssue" regardless of caller, which
    // silently matched nothing for non-UAE countries (same class of bug as the
    // main PalletNo/BuildDate/GIN table fixes).
    private static void AppendCommonFilters(
        StringBuilder sb, DynamicParameters p, TransferHistoryFilter f, string buildTable, bool includeStoreFilter = true)
    {
        if (includeStoreFilter && !string.IsNullOrWhiteSpace(f.Store))
        {
            sb.Append("\n   AND e.ShopName = @store");
            p.Add("@store", f.Store);
        }

        if (f.WithoutPallet)
            sb.Append($"\n   AND NOT EXISTS (SELECT 1 FROM {buildTable} WHERE TrfNo = a.TrfNo)");

        if (f.WithoutGin)
            sb.Append("\n   AND c.SrNo IS NULL");

        if (f.WithoutGrn)
            sb.Append("\n   AND d.EntryNo IS NULL");

        if (!string.IsNullOrWhiteSpace(f.SearchValue))
        {
            p.Add("@search", $"%{f.SearchValue.Trim()}%");
            sb.Append(f.SearchBy switch
            {
                "PalletNo" => "\n   AND b.PalletNo LIKE @search",
                "GIN"      => "\n   AND CAST(c.SrNo AS nvarchar(50)) LIKE @search",
                "GRN"      => "\n   AND CAST(d.EntryNo AS nvarchar(50)) LIKE @search",
                _          => "\n   AND a.TrfNo LIKE @search",
            });
        }
    }

    // ── Filtered summary/breakdown (Without Pallet/GIN/GRN + Search) ───────────
    //
    // Builds "…;WITH Eligible AS (…)" using the SAME header/join shape and
    // Without Pallet/GIN/GRN + Search filters as the Detailed view (via
    // AppendCommonFilters above) — used whenever any of those filters is
    // active, so the summary cards, By Store/By Country breakdown, and the
    // Division/Department/GroupCode/Brand popup are scoped to the exact same
    // transfers the Detailed view shows for the same filter selection.
    //
    // costCodeTo/locCodeTo: store-scoped (exact match) — pass both, leave
    // whCostCodeTo/whLocCodeTo null. whCostCodeTo/whLocCodeTo: whole-country
    // (warehouse-excluded) — pass both, leave costCodeTo/locCodeTo null.
    //
    // requireGin: true for the GIN breakdown popup specifically — restricts the
    // eligible set to transfers that also have a linked GIN, so its Grand Total
    // doesn't silently include item quantity from transfers with no GIN at all
    // (which would inflate it beyond what the GIN Qty card shows). Not needed
    // for the summary query (FilteredSummarySql below), where GIN Count/Qty
    // naturally comes out 0 for eligible transfers with no matching GIN row.
    //
    // Carries the SAME per-row GIN match the Detail Rows query itself uses
    // (c.SrNo/c.Qty, correlated via c.TrfNo = a.TrfNo AND c.EntryDate >=
    // a.TrfDate) straight through as GinSrNo/GinQty, rather than re-deriving it
    // later via a bare "TrfNo IN (...)" against {ginTable} — TrfNo values get
    // reused over time, so an un-correlated re-query can pick up an unrelated
    // GIN from a completely different occurrence of the same TrfNo, even for a
    // transfer the correlated join correctly found no GIN for (observed: GIN
    // Count/Qty stayed non-zero with Without GIN checked, for exactly this
    // reason). Not deduped to DISTINCT TrfNo here — a transfer can fan out to
    // several GIN rows (same shape the Detail Rows query already returns, one
    // row per TrfNo+GIN pair) — callers COUNT(DISTINCT TrfNo)/GinSrNo as needed.
    private static string BuildEligibleTrfNoCte(
        string transferHeaderTable, string buildTable, string ginTable, string grnTable,
        TransferHistoryFilter f, DateTime from, DateTime to,
        string? costCodeTo, string? locCodeTo, string? whCostCodeTo, string? whLocCodeTo,
        bool requireGin, out DynamicParameters p)
    {
        p = new DynamicParameters();
        p.Add("@costCodeTo", costCodeTo);
        p.Add("@locCodeTo", locCodeTo);
        p.Add("@whCostCodeTo", whCostCodeTo);
        p.Add("@whLocCodeTo", whLocCodeTo);

        // Search ignores the date range entirely, same as BuildSqlOnPrem/BuildSqlCountry.
        var hasSearch  = !string.IsNullOrWhiteSpace(f.SearchValue);
        var dateFilter = hasSearch ? "" : "\n       AND a.TrfDate >= @from AND a.TrfDate <= @to";
        if (!hasSearch) { p.Add("@from", from); p.Add("@to", to.AddDays(1).AddSeconds(-1)); }

        var sb = new StringBuilder($@"
;WITH Eligible AS (
    SELECT a.TrfNo, c.SrNo AS GinSrNo, c.Qty AS GinQty
      FROM {transferHeaderTable}                    a WITH (NOLOCK)
      LEFT JOIN {ginTable}                           c ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
      LEFT JOIN {grnTable}                           d ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
     WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
       AND (@costCodeTo   IS NULL OR a.CostCodeTo = @costCodeTo)
       AND (@locCodeTo    IS NULL OR a.LocCodeTo  = @locCodeTo)
       AND (@whCostCodeTo IS NULL OR a.CostCodeTo <> @whCostCodeTo)
       AND (@whLocCodeTo  IS NULL OR a.LocCodeTo  <> @whLocCodeTo)");

        AppendCommonFilters(sb, p, f, buildTable, includeStoreFilter: false);
        if (requireGin) sb.Append("\n       AND c.SrNo IS NOT NULL");
        sb.Append("\n)");
        return sb.ToString();
    }

    private static string FilteredSummarySql(string transferDetailTable) => $@"
        SELECT
            (SELECT COUNT(DISTINCT TrfNo) FROM Eligible) AS TransferCount,
            ISNULL((SELECT SUM(Quantity) FROM {transferDetailTable} WITH (NOLOCK) WHERE TrfNo IN (SELECT DISTINCT TrfNo FROM Eligible)), 0) AS TransferQty,
            (SELECT COUNT(DISTINCT GinSrNo) FROM Eligible WHERE GinSrNo IS NOT NULL) AS GinCount,
            ISNULL((SELECT SUM(GinQty) FROM (SELECT DISTINCT GinSrNo, GinQty FROM Eligible WHERE GinSrNo IS NOT NULL) x), 0) AS GinQty";

    private static string FilteredGroupCodeSql(string transferDetailTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfNo IN (SELECT DISTINCT TrfNo FROM Eligible)
         GROUP BY vtd.groupcode";
}
