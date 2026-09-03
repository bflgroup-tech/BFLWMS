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

    // Per-country totals, scoped to Country + date range only (no Store/search/
    // without-flags — these are top-of-page KPI cards, not tied to the detail
    // table's extra filters). countries: null/empty = every SIM country.
    public async Task<TransferSummaryResult> GetTransferSummaryAsync(
        IReadOnlyCollection<string>? countries, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        var list = countries is { Count: > 0 } ? countries.ToList() : await GetCountriesAsync(ct);
        var warnings = new List<string>();

        var tasks = list.Select(async country =>
        {
            try { return await GetSummaryForCountryAsync(country, dateFrom.Date, dateTo.Date, ct); }
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
        string country, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        if (country == UaeCountry)
            return await GetCountrySummaryOnPremAsync(country, dateFrom, dateTo, ct);

        var (histFrom, histTo, includesToday) = SplitDateRange(dateFrom, dateTo);
        var histTask = histFrom is not null
            ? GetCountrySummaryOnPremAsync(country, histFrom.Value, histTo!.Value, ct)
            : Task.FromResult(new TransferSummary(country, 0, 0, 0, 0));
        var todayTask = includesToday
            ? GetCountrySummaryRegionalAsync(country, DateTime.Today, DateTime.Today, ct)
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
        string country, DateTime from, DateTime to, CancellationToken ct)
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

        var toEnd = to.AddDays(1).AddSeconds(-1);

        var wh = await WithOnPremAsync(conn =>
            ResolveWarehouseCodeAsync(conn, "BFLDATA.dbo.DataSettings", country, ct), ct);

        // GIN correlates via TrfNo -> this DataName's OWN transferheader
        // (same relationship the detail table's GIN join and
        // GetOneStoreSummaryAsync use) — NOT vGoodsIssueplt.ShopIssue, which
        // doesn't reliably equal ShopName outside UAE.
        var perDataNameTask = Task.WhenAll(dataNames.Select(dn =>
        {
            // GIN lives in the central BFLDATA.dbo db for UAE, but in each
            // country's OWN DataName db for everyone else — same "wrong db"
            // mistake already caught and fixed for vTransferDetail, just
            // missed here too. Confirmed directly: bflksa..vGoodsIssuePlt,
            // not BFLDATA.dbo.vGoodsIssueplt, for KSA.
            var ginTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{dn}]..vGoodsIssueplt";
            return WithOnPremAsync(async conn =>
            {
                var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                    TransferSummarySql($"[{dn}]..vTransferDetail"),
                    new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                // Same TrfNo-set approach as GetOneStoreSummaryAsync: this
                // DataName's transfers created in [from, to] first (cheap),
                // then GIN activity for exactly that TrfNo set — no
                // correlated per-row subquery.
                var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition($@"
                    SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
                      FROM {ginTable} c WITH (NOLOCK)
                     WHERE c.TrfNo IN (
                         SELECT TrfNo FROM [{dn}]..transferheader WITH (NOLOCK)
                          WHERE TrfDate >= @from AND TrfDate <= @to
                     )",
                    new { from, to = toEnd }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

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

    // Regional server path (non-UAE only) — today's slice.
    private async Task<TransferSummary> GetCountrySummaryRegionalAsync(
        string country, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);
        var wh = await ResolveWarehouseCodeAsync(conn, "BFLDATA..DataSettings", country: null, ct);
        var toEnd = to.AddDays(1).AddSeconds(-1);

        var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
            TransferSummarySql("vTransferDetail"),
            new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // vGoodsIssueplt lives in the country's own dataName db here (same as
        // transferheader/vTransferDetail), NOT the sibling BFLDATA db —
        // confirmed directly: bflksa..vGoodsIssuePlt, not BFLDATA..vgoodsissueplt.
        var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
            SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
              FROM vgoodsissueplt WITH (NOLOCK)
             WHERE TrfNo IN (
                 SELECT TrfNo FROM transferheader WITH (NOLOCK)
                  WHERE TrfDate >= @from AND TrfDate <= @to
             )",
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
    // GIN is scoped by the underlying TRANSFER's own date+store (TrfDate,
    // CostCodeTo/LocCodeTo on transferHeaderTable), not the GIN's own
    // EntryDate — see the query below for why (avoids both a slow correlated
    // subquery and TrfNo-reuse ambiguity between stores).
    private static async Task<TransferSummary> GetOneStoreSummaryAsync(
        SqlConnection conn, string shopName, string transferDetailTable, string transferHeaderTable, string ginTable,
        string costCodeTo, string locCodeTo, DateTime from, DateTime to, CancellationToken ct)
    {
        var toEnd = to.AddDays(1).AddSeconds(-1);
        var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
            StoreTransferSummarySql(transferDetailTable),
            new { from, to = toEnd, costCodeTo, locCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // Scoped by the TRANSFER's own date/store (TrfDate + CostCodeTo/LocCodeTo),
        // not the GIN's own EntryDate: get this store's transfers created in
        // [from, to] first (cheap — same filter as the transfer-count query
        // above), then look up GIN activity for exactly that TrfNo set. No
        // correlated per-row subquery, and no reuse ambiguity — the TrfNo set
        // is already uniquely scoped to this store before vGoodsIssueplt is
        // even touched.
        var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition($@"
            SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
              FROM {ginTable} c WITH (NOLOCK)
             WHERE c.TrfNo IN (
                 SELECT TrfNo FROM {transferHeaderTable} WITH (NOLOCK)
                  WHERE TrfDate >= @from AND TrfDate <= @to
                    AND CostCodeTo = @costCodeTo AND LocCodeTo = @locCodeTo
             )",
            new { costCodeTo, locCodeTo, from, to = toEnd }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new TransferSummary(shopName, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
    }

    // GIN lives centrally (BFLDATA.dbo) for UAE, but in the shop's own
    // DataName db for everyone else — same "wrong db" mistake already fixed
    // for vTransferDetail, just missed here too (confirmed: bflksa..vGoodsIssuePlt).
    private Task<TransferSummary> GetOneStoreSummaryOnPremAsync(
        string country, StoreRow s, DateTime from, DateTime to, CancellationToken ct)
    {
        var ginTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{s.DataName}]..vGoodsIssueplt";
        return WithOnPremAsync(conn => GetOneStoreSummaryAsync(
            conn, s.ShopName, $"[{s.DataName}]..vTransferDetail", $"[{s.DataName}]..transferheader", ginTable,
            s.CostCodeTo, s.LocCodeTo, from, to, ct), ct);
    }

    // Regional (today-only) — reuses the shop's CostCodeTo/LocCodeTo already
    // resolved from OnPremBackup's DataSettings rather than re-querying the
    // regional server for it: DataSettings is reference/master data, not subject
    // to the same same-day sync lag that transactional tables are. vGoodsIssueplt
    // lives in the country's own dataName db here too, not the sibling BFLDATA db.
    private async Task<TransferSummary> GetOneStoreSummaryRegionalAsync(
        string country, string dataName, StoreRow s, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var conn = OpenCountryWithDataName(country, dataName);
        return await GetOneStoreSummaryAsync(
            conn, s.ShopName, "vTransferDetail", "transferheader", "vgoodsissueplt",
            s.CostCodeTo, s.LocCodeTo, from, to, ct);
    }

    /// <summary>
    /// Same metrics as GetTransferSummaryAsync but scoped to one specific store
    /// (its own CostCodeTo/LocCodeTo, same precision fix as the detail query) —
    /// shown as an extra card alongside the country card when a store is picked.
    /// Returns null if the store can't be resolved in DataSettings.
    /// </summary>
    public async Task<TransferSummary?> GetStoreSummaryAsync(
        string country, string store, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        var from = dateFrom.Date;
        var to   = dateTo.Date;

        StoreRow? s;
        await using (var onprem = OpenOnPrem())
        {
            s = (await GetStoresOnPremAsync(onprem, country, store, ct)).SingleOrDefault();
        }
        if (s is null) return null;

        if (country == UaeCountry)
            return await GetOneStoreSummaryOnPremAsync(country, s, from, to, ct);

        var (histFrom, histTo, includesToday) = SplitDateRange(from, to);
        var histTask = histFrom is not null
            ? GetOneStoreSummaryOnPremAsync(country, s, histFrom.Value, histTo!.Value, ct)
            : Task.FromResult(new TransferSummary(store, 0, 0, 0, 0));
        var todayTask = includesToday
            ? GetTodayStoreSummaryRegionalAsync(country, s, ct)
            : Task.FromResult(new TransferSummary(store, 0, 0, 0, 0));

        await Task.WhenAll(histTask, todayTask);
        var h = histTask.Result; var t = todayTask.Result;
        return new TransferSummary(store,
            h.TransferCount + t.TransferCount, h.TransferQty + t.TransferQty,
            h.GinCount + t.GinCount, h.GinQty + t.GinQty);
    }

    private async Task<TransferSummary> GetTodayStoreSummaryRegionalAsync(
        string country, StoreRow s, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        var today = DateTime.Today;
        return await GetOneStoreSummaryRegionalAsync(country, dataName, s, today, today, ct);
    }

    /// <summary>
    /// Per-STORE breakdown within a single country — same shape as
    /// GetTransferSummaryAsync's per-country breakdown, just one level down.
    /// Shown below the total cards when a single country is selected with
    /// "(All stores)".
    /// </summary>
    public async Task<List<TransferSummary>> GetStoreSummariesAsync(
        string country, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        var from = dateFrom.Date;
        var to   = dateTo.Date;

        List<StoreRow> stores;
        await using (var onprem = OpenOnPrem())
        {
            stores = await GetStoresOnPremAsync(onprem, country, store: null, ct);
        }

        if (country == UaeCountry)
        {
            var tasks = stores.Select(s => GetOneStoreSummaryOnPremAsync(country, s, from, to, ct));
            var results = await Task.WhenAll(tasks);
            return results.OrderBy(r => r.Country).ToList();
        }
        else
        {
            var (histFrom, histTo, includesToday) = SplitDateRange(from, to);

            var histTasks = histFrom is not null
                ? stores.Select(s => GetOneStoreSummaryOnPremAsync(country, s, histFrom.Value, histTo!.Value, ct)).ToArray()
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
                todayTasks = stores.Select(s => GetOneStoreSummaryRegionalAsync(country, dataName, s, today, today, ct)).ToArray();
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
        IReadOnlyCollection<string>? countries, string? store, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
        => GetBreakdownAsync(countries, store, dateFrom, dateTo, byGin: false, ct);

    public Task<TransferGinBreakdownResult> GetGinBreakdownAsync(
        IReadOnlyCollection<string>? countries, string? store, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
        => GetBreakdownAsync(countries, store, dateFrom, dateTo, byGin: true, ct);

    private async Task<TransferGinBreakdownResult> GetBreakdownAsync(
        IReadOnlyCollection<string>? countries, string? store, DateTime dateFrom, DateTime dateTo, bool byGin, CancellationToken ct)
    {
        var list = countries is { Count: > 0 } ? countries.ToList() : await GetCountriesAsync(ct);
        var warnings = new List<string>();

        var tasks = list.Select(async country =>
        {
            try { return await GetBreakdownChunksForCountryAsync(country, store, dateFrom.Date, dateTo.Date, byGin, ct); }
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
    // (whole country, warehouse-excluded) — same historical/today split, just
    // returning per-GroupCode chunks instead of one aggregate.
    private async Task<List<GroupCodeChunkRow>> GetBreakdownChunksForCountryAsync(
        string country, string? store, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(store))
        {
            StoreRow? s;
            await using (var onprem = OpenOnPrem())
            {
                s = (await GetStoresOnPremAsync(onprem, country, store, ct)).SingleOrDefault();
            }
            if (s is null) return [];

            if (country == UaeCountry)
                return await GetOneStoreBreakdownChunksOnPremAsync(country, s, from, to, byGin, ct);

            var (histFrom, histTo, includesToday) = SplitDateRange(from, to);
            var histTask = histFrom is not null
                ? GetOneStoreBreakdownChunksOnPremAsync(country, s, histFrom.Value, histTo!.Value, byGin, ct)
                : Task.FromResult(new List<GroupCodeChunkRow>());
            var todayTask = includesToday
                ? GetOneStoreBreakdownChunksRegionalAsync(country, s, byGin, ct)
                : Task.FromResult(new List<GroupCodeChunkRow>());

            await Task.WhenAll(histTask, todayTask);
            return histTask.Result.Concat(todayTask.Result).ToList();
        }

        if (country == UaeCountry)
            return await GetCountryBreakdownChunksOnPremAsync(country, from, to, byGin, ct);

        var (chHistFrom, chHistTo, chIncludesToday) = SplitDateRange(from, to);
        var chHistTask = chHistFrom is not null
            ? GetCountryBreakdownChunksOnPremAsync(country, chHistFrom.Value, chHistTo!.Value, byGin, ct)
            : Task.FromResult(new List<GroupCodeChunkRow>());
        var chTodayTask = chIncludesToday
            ? GetCountryBreakdownChunksRegionalAsync(country, DateTime.Today, DateTime.Today, byGin, ct)
            : Task.FromResult(new List<GroupCodeChunkRow>());

        await Task.WhenAll(chHistTask, chTodayTask);
        return chHistTask.Result.Concat(chTodayTask.Result).ToList();
    }

    private Task<List<GroupCodeChunkRow>> GetOneStoreBreakdownChunksOnPremAsync(
        string country, StoreRow s, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        var ginTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{s.DataName}]..vGoodsIssueplt";
        var toEnd = to.AddDays(1).AddSeconds(-1);
        var sql = byGin
            ? StoreGinGroupCodeSql($"[{s.DataName}]..vTransferDetail", $"[{s.DataName}]..transferheader", ginTable)
            : StoreTransferGroupCodeSql($"[{s.DataName}]..vTransferDetail");
        return WithOnPremAsync(async conn =>
        {
            var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
                sql, new { from, to = toEnd, costCodeTo = s.CostCodeTo, locCodeTo = s.LocCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }, ct);
    }

    private async Task<List<GroupCodeChunkRow>> GetOneStoreBreakdownChunksRegionalAsync(
        string country, StoreRow s, bool byGin, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        var today = DateTime.Today;
        var toEnd = today.AddDays(1).AddSeconds(-1);
        await using var conn = OpenCountryWithDataName(country, dataName);
        var sql = byGin
            ? StoreGinGroupCodeSql("vTransferDetail", "transferheader", "vgoodsissueplt")
            : StoreTransferGroupCodeSql("vTransferDetail");
        var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
            sql, new { from = today, to = toEnd, costCodeTo = s.CostCodeTo, locCodeTo = s.LocCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // Loops DISTINCT DataName the same way GetCountrySummaryOnPremAsync does —
    // warehouse-excluded, not scoped to any one shop.
    private async Task<List<GroupCodeChunkRow>> GetCountryBreakdownChunksOnPremAsync(
        string country, DateTime from, DateTime to, bool byGin, CancellationToken ct)
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

        var toEnd = to.AddDays(1).AddSeconds(-1);
        var wh = await WithOnPremAsync(conn =>
            ResolveWarehouseCodeAsync(conn, "BFLDATA.dbo.DataSettings", country, ct), ct);

        var perDataNameTask = Task.WhenAll(dataNames.Select(dn =>
        {
            var ginTable = country == UaeCountry ? "BFLDATA.dbo.vGoodsIssueplt" : $"[{dn}]..vGoodsIssueplt";
            var sql = byGin
                ? GinGroupCodeSql($"[{dn}]..vTransferDetail", $"[{dn}]..transferheader", ginTable)
                : TransferGroupCodeSql($"[{dn}]..vTransferDetail");
            return WithOnPremAsync(async conn =>
            {
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
        string country, DateTime from, DateTime to, bool byGin, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

        await using var conn = OpenCountryWithDataName(country, dataName);
        var wh = await ResolveWarehouseCodeAsync(conn, "BFLDATA..DataSettings", country: null, ct);
        var toEnd = to.AddDays(1).AddSeconds(-1);

        var sql = byGin
            ? GinGroupCodeSql("vTransferDetail", "transferheader", "vgoodsissueplt")
            : TransferGroupCodeSql("vTransferDetail");
        var rows = await conn.QueryAsync<GroupCodeChunkRow>(new CommandDefinition(
            sql, new { from, to = toEnd, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
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

    // GIN has no line-item GroupCode of its own — scoped to the TrfNo set that
    // has at least one linked GIN (same TrfNo-set approach the GIN Count/Qty
    // cards themselves use), then rolled up via that transfer's own vTransferDetail
    // lines. Same approximation ShipmentStatusService's GIN-flow Division rollup
    // already uses.
    private static string GinGroupCodeSql(string transferDetailTable, string transferHeaderTable, string ginTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfNo IN (
             SELECT th.TrfNo FROM {transferHeaderTable} th WITH (NOLOCK)
              WHERE th.TrfDate >= @from AND th.TrfDate <= @to
                AND (@whCostCodeTo IS NULL OR th.CostCodeTo <> @whCostCodeTo)
                AND (@whLocCodeTo  IS NULL OR th.LocCodeTo  <> @whLocCodeTo)
                AND EXISTS (SELECT 1 FROM {ginTable} g WHERE g.TrfNo = th.TrfNo)
         )
         GROUP BY vtd.groupcode";

    private static string StoreGinGroupCodeSql(string transferDetailTable, string transferHeaderTable, string ginTable) => $@"
        SELECT vtd.groupcode AS GroupCode, ISNULL(SUM(vtd.Quantity),0) AS Qty
          FROM {transferDetailTable} vtd WITH (NOLOCK)
         WHERE vtd.TrfNo IN (
             SELECT th.TrfNo FROM {transferHeaderTable} th WITH (NOLOCK)
              WHERE th.TrfDate >= @from AND th.TrfDate <= @to
                AND th.CostCodeTo = @costCodeTo AND th.LocCodeTo = @locCodeTo
                AND EXISTS (SELECT 1 FROM {ginTable} g WHERE g.TrfNo = th.TrfNo)
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

        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfDate, a.TrfNo, c.SrNo) SrNo,
       @shopName ShopName,
       a.TrfNo,
       a.TrfDate,
       Quantity  = CAST(ISNULL((SELECT SUM(Quantity) FROM [{s.DataName}]..vTransferDetail WHERE TrfNo = a.TrfNo), 0) AS INT),
       PalletNo  = (SELECT TOP 1 PalletNo FROM {buildTable} WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM [{s.DataName}]..transferheader           a
  LEFT JOIN (
      SELECT TrfNo, PalletNo, EntryDate,
             ROW_NUMBER() OVER (PARTITION BY TrfNo ORDER BY PalletNo DESC) rn
        FROM {buildTable}
  )                                            b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
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
       PalletNo  = (SELECT TOP 1 PalletNo FROM vGoodsIssue WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM transferheader              a
  LEFT JOIN (
      SELECT TrfNo, PalletNo, EntryDate,
             ROW_NUMBER() OVER (PARTITION BY TrfNo ORDER BY PalletNo DESC) rn
        FROM vGoodsIssue
  )                                b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
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
}
