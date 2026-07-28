using System.Text;
using System.Text.RegularExpressions;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Transfer / GIN / GRN History report.
///
/// ALL queries run against OnPremBackup (192.168.5.61) where:
///   - BFLDATA is a local database (has SIMCountry, vGoodsIssue, vGoodsIssueplt)
///   - [{DataName}].. references are linked-server / cross-DB names (e.g. bflksa)
///
/// The per-country connection strings (KSA_DB_ConnectionString etc.) are NOT
/// used here — they point to separate servers whose local BFLDATA lacks SIMCountry
/// and whose views do not work in isolation.
/// </summary>
public class TransferGinGrnService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    // UAE has no dedicated connection string — always use OnPremBackup for it.
    private const string UaeCountry = "UAE";

    private SqlConnection OpenOnPrem()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private SqlConnection OpenCountry(string country)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
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
    /// Stores for the country.
    /// UAE → OnPremBackup with SIMCountry filter (no dedicated UAE connection string).
    /// Others → country server's local BFLDATA.dbo.DataSettings, which only contains
    ///          that country's stores so no SIMCountry filter is needed.
    /// </summary>
    public async Task<List<string>> GetStoresAsync(string country, CancellationToken ct = default)
    {
        if (country == UaeCountry)
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
        else
        {
            await using var c = OpenCountry(country);
            var rows = await c.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT ShopName
                  FROM BFLDATA.dbo.DataSettings
                 WHERE ShopName IS NOT NULL AND ShopName <> ''
                   AND Concept  <> 'Warehouse'
                 ORDER BY ShopName",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
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
            // Several UAE shops can share the same physical linked-server DataName
            // (multiple ShopNames -> one DB) — looping by distinct DataName and then
            // disambiguating rows afterward with only "a.CostCodeTo = e.CostCodeTo"
            // let a transfer whose CostCodeTo happened to match a DIFFERENT shop's
            // code (but a different LocCodeTo) leak into the wrong store's results.
            // Resolve each shop's own (DataName, CostCodeTo, LocCodeTo) up front and
            // scope the transferheader query to exactly those values instead.
            List<UaeStoreRow> uaeStores;
            await using (var onprem = OpenOnPrem())
            {
                uaeStores = await GetUaeStoresAsync(onprem, f.Store, ct);
            }

            // One connection per shop, run concurrently — a single SqlConnection
            // can't run concurrent commands, and going one at a time here was the
            // main reason a UAE ("every store") load took so long: each linked-server
            // round trip against OnPremBackup was paid serially instead of in parallel.
            var perStore = await Task.WhenAll(uaeStores.Select(async s =>
            {
                await using var conn = OpenOnPrem();
                var sql  = BuildSqlOnPrem(s, f, out var parms);
                var rows = await conn.QueryAsync<TransferHistoryRow>(
                    new CommandDefinition(sql, parms,
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return rows.AsList();
            }));
            return perStore.SelectMany(r => r).ToList();
        }
        else
        {
            // Non-UAE: connect directly to the country server, but override the
            // default database to the country's actual DB name (e.g. "bflksa").
            // The connection string may point to a different default DB, so we
            // resolve the real name from OnPremBackup first.
            await using var onprem = OpenOnPrem();
            var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
            if (string.IsNullOrWhiteSpace(dataName))
                throw new InvalidOperationException(
                    $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

            var csb = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            {
                InitialCatalog = dataName,
                ConnectTimeout = ConnectTimeoutSeconds
            };
            await using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();

            var sql  = BuildSqlCountry(f, out var parms);
            var rows = await conn.QueryAsync<TransferHistoryRow>(
                new CommandDefinition(sql, parms,
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }
    }

    // ── Summary cards (Transfer Count / Transfer Qty / GIN Qty per country) ────

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
        var from = dateFrom.Date;
        var to   = dateTo.Date.AddDays(1).AddSeconds(-1);
        var warnings = new List<string>();

        var tasks = list.Select(async country =>
        {
            try { return await GetSummaryForCountryAsync(country, from, to, ct); }
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
    // (e.g. KSA is '005'/'05'), so it must be looked up, never hardcoded.
    private static async Task<WarehouseCode> ResolveWarehouseCodeAsync(
        SqlConnection conn, string dataSettingsTable, string? simCountry, CancellationToken ct)
    {
        var simFilter = simCountry is null ? "" : " AND SIMCountry = @simCountry";
        var sql = $@"
            SELECT TOP 1 CostCodeTo, LocCodeTo
              FROM {dataSettingsTable} WITH (NOLOCK)
             WHERE Concept = 'Warehouse'{simFilter}";
        var row = await conn.QuerySingleOrDefaultAsync<WarehouseCode>(new CommandDefinition(
            sql, new { simCountry }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return row ?? new WarehouseCode();
    }

    // dataNameTable e.g. "[EX2KSA]..vTransferDetail" (UAE linked server) or just
    // "vTransferDetail" (non-UAE: lives in the country's own dataName database,
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
        string country, DateTime from, DateTime to, CancellationToken ct)
    {
        if (country == UaeCountry)
        {
            List<string> uaeDataNames;
            await using (var onprem = OpenOnPrem())
            {
                uaeDataNames = (await onprem.QueryAsync<string>(new CommandDefinition(@"
                    SELECT DISTINCT DataName
                      FROM BFLDATA.dbo.DataSettings
                     WHERE SIMCountry = 'UAE'
                       AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
            }

            // Warehouse-code lookup, per-DataName transfer totals, and the GIN qty
            // query are all independent — run them concurrently (own connection
            // each) instead of serially against the same connection.
            var whTask = Task.Run(async () =>
            {
                await using var conn = OpenOnPrem();
                return await ResolveWarehouseCodeAsync(conn, "BFLDATA.dbo.DataSettings", UaeCountry, ct);
            });
            var ginTask = Task.Run(async () =>
            {
                await using var conn = OpenOnPrem();
                return await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
                    SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
                      FROM BFLDATA.dbo.vGoodsIssueplt c WITH (NOLOCK)
                      JOIN BFLDATA.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = c.ShopIssue
                     WHERE ds.SIMCountry = 'UAE' AND c.EntryDate >= @from AND c.EntryDate <= @to",
                    new { from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            });

            var wh = await whTask;
            var perStoreTask = Task.WhenAll(uaeDataNames.Select(async dn =>
            {
                await using var conn = OpenOnPrem();
                return await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                    TransferSummarySql($"[{dn}]..vTransferDetail"),
                    new { from, to, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }));

            await Task.WhenAll(perStoreTask, ginTask);
            var transferCount = perStoreTask.Result.Sum(r => r.TransferCount);
            var transferQty   = perStoreTask.Result.Sum(r => r.TransferQty ?? 0);

            return new TransferSummary(country, transferCount, transferQty, ginTask.Result.GinCount, ginTask.Result.GinQty ?? 0);
        }
        else
        {
            await using var onprem = OpenOnPrem();
            var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
            if (string.IsNullOrWhiteSpace(dataName))
                throw new InvalidOperationException(
                    $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

            var csb = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            {
                InitialCatalog = dataName,
                ConnectTimeout = ConnectTimeoutSeconds
            };
            await using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();

            var wh = await ResolveWarehouseCodeAsync(conn, "BFLDATA..DataSettings", simCountry: null, ct);

            var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                TransferSummarySql("vTransferDetail"),
                new { from, to, whCostCodeTo = wh.CostCodeTo, whLocCodeTo = wh.LocCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
                SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
                  FROM BFLDATA..vgoodsissueplt WITH (NOLOCK)
                 WHERE EntryDate >= @from AND EntryDate <= @to",
                new { from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            return new TransferSummary(country, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
        }
    }

    private static string StoreTransferSummarySql(string transferDetailTable) => $@"
        SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
          FROM {transferDetailTable} WITH (NOLOCK)
         WHERE TrfDate >= @from AND TrfDate <= @to
           AND CostCodeTo = @costCodeTo AND LocCodeTo = @locCodeTo";

    /// <summary>
    /// Same 3 metrics as GetTransferSummaryAsync but scoped to one specific store
    /// (its own CostCodeTo/LocCodeTo, same precision fix as the detail query) —
    /// shown as an extra card alongside the country card when a store is picked.
    /// Returns null if the store can't be resolved in DataSettings.
    /// </summary>
    public async Task<TransferSummary?> GetStoreSummaryAsync(
        string country, string store, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        var from = dateFrom.Date;
        var to   = dateTo.Date.AddDays(1).AddSeconds(-1);

        if (country == UaeCountry)
        {
            UaeStoreRow? s;
            await using (var onprem = OpenOnPrem())
            {
                s = (await GetUaeStoresAsync(onprem, store, ct)).SingleOrDefault();
            }
            if (s is null) return null;

            await using var conn = OpenOnPrem();
            var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                StoreTransferSummarySql($"[{s.DataName}]..vTransferDetail"),
                new { from, to, costCodeTo = s.CostCodeTo, locCodeTo = s.LocCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
                SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
                  FROM BFLDATA.dbo.vGoodsIssueplt WITH (NOLOCK)
                 WHERE ShopIssue = @store AND EntryDate >= @from AND EntryDate <= @to",
                new { store, from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            return new TransferSummary(store, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
        }
        else
        {
            await using var onprem = OpenOnPrem();
            var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, country, ct);
            if (string.IsNullOrWhiteSpace(dataName))
                throw new InvalidOperationException(
                    $"No DataName found in BFLDATA.dbo.DataSettings for country '{country}'.");

            var csb = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            {
                InitialCatalog = dataName,
                ConnectTimeout = ConnectTimeoutSeconds
            };
            await using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();

            var storeCode = await conn.QuerySingleOrDefaultAsync<WarehouseCode>(new CommandDefinition(@"
                SELECT TOP 1 CostCodeTo, LocCodeTo FROM BFLDATA..DataSettings WHERE ShopName = @store",
                new { store }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (storeCode is null) return null;

            var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                StoreTransferSummarySql("vTransferDetail"),
                new { from, to, costCodeTo = storeCode.CostCodeTo, locCodeTo = storeCode.LocCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var gin = await conn.QuerySingleAsync<GinCountQtyRow>(new CommandDefinition(@"
                SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
                  FROM BFLDATA..vgoodsissueplt WITH (NOLOCK)
                 WHERE ShopIssue = @store AND EntryDate >= @from AND EntryDate <= @to",
                new { store, from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            return new TransferSummary(store, transferRow.TransferCount, transferRow.TransferQty ?? 0, gin.GinCount, gin.GinQty ?? 0);
        }
    }

    // ── SQL builders ─────────────────────────────────────────────────────────

    // OnPremBackup path: tables accessed via linked-server prefix [{dataName}].
    private record UaeStoreRow(string ShopName, string DataName, string CostCodeTo, string LocCodeTo);

    // One row per UAE shop (never per DataName — a DataName can be shared by
    // several shops), so each shop's own CostCodeTo/LocCodeTo is known up front
    // rather than re-derived via a loose join later. store: null = every UAE shop.
    private static async Task<List<UaeStoreRow>> GetUaeStoresAsync(
        SqlConnection onprem, string? store, CancellationToken ct)
    {
        var storeFilter = string.IsNullOrWhiteSpace(store) ? "" : "\n   AND ShopName = @store";
        var sql = $@"
            SELECT ShopName, DataName, CostCodeTo, LocCodeTo
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry = 'UAE'
               AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''
               AND Concept <> 'Warehouse'{storeFilter}";
        var rows = await onprem.QueryAsync<UaeStoreRow>(new CommandDefinition(
            sql, new { store }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // UAE path: shop's own CostCodeTo/LocCodeTo (resolved via GetUaeStoresAsync)
    // scope the query directly — no join back to DataSettings, so a transfer can
    // no longer leak in just because its CostCodeTo coincidentally matches some
    // OTHER shop sharing this DataName.
    private static string BuildSqlOnPrem(UaeStoreRow s, TransferHistoryFilter f, out DynamicParameters p)
    {
        p = new DynamicParameters();
        p.Add("@shopName",   s.ShopName);
        p.Add("@costCodeTo", s.CostCodeTo);
        p.Add("@locCodeTo",  s.LocCodeTo);
        var hasSearch    = !string.IsNullOrWhiteSpace(f.SearchValue);
        var dateFilter   = hasSearch ? "" : "\n   AND a.TrfDate >= @from AND a.TrfDate <= @to";
        if (!hasSearch) { p.Add("@from", f.DateFrom.Date); p.Add("@to", f.DateTo.Date.AddDays(1).AddSeconds(-1)); }

        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfDate, a.TrfNo, c.SrNo) SrNo,
       @shopName ShopName,
       a.TrfNo,
       a.TrfDate,
       PalletNo  = (SELECT TOP 1 PalletNo FROM BFLDATA.dbo.vGoodsIssue WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
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
        FROM BFLDATA.dbo.vGoodsIssue
  )                                            b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
  LEFT JOIN BFLDATA.dbo.vGoodsIssueplt        c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..GRNHeaderRF          d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..TransferReverse      f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND a.CostCodeTo = @costCodeTo AND a.LocCodeTo = @locCodeTo");

        AppendCommonFilters(sb, p, f, includeStoreFilter: false);
        sb.Append("\n ORDER BY a.TrfDate, a.TrfNo");
        return sb.ToString();
    }

    // Country-server path (non-UAE): all tables are local — no linked-server prefix.
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
       PalletNo  = (SELECT TOP 1 PalletNo FROM BFLDATA..vGoodsIssue WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
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
        FROM BFLDATA..vGoodsIssue
  )                                b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
  LEFT JOIN BFLDATA..vGoodsIssueplt c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN GRNHeaderRF             d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  JOIN  BFLDATA..DataSettings       e  ON a.CostCodeTo = e.CostCodeTo
  LEFT JOIN TransferReverse         f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND e.ShopName NOT IN (
       SELECT ShopName FROM BFLDATA..DataSettings WHERE Concept = 'Warehouse'
   )");

        AppendCommonFilters(sb, p, f);
        sb.Append("\n ORDER BY a.TrfDate, a.TrfNo");
        return sb.ToString();
    }

    // includeStoreFilter: false for the UAE path, where the query is already
    // scoped to one shop's own CostCodeTo/LocCodeTo (see BuildSqlOnPrem) — there's
    // no "e" (DataSettings) alias left to filter on there.
    private static void AppendCommonFilters(
        StringBuilder sb, DynamicParameters p, TransferHistoryFilter f, bool includeStoreFilter = true)
    {
        if (includeStoreFilter && !string.IsNullOrWhiteSpace(f.Store))
        {
            sb.Append("\n   AND e.ShopName = @store");
            p.Add("@store", f.Store);
        }

        if (f.WithoutPallet)
            sb.Append("\n   AND NOT EXISTS (SELECT 1 FROM BFLDATA..vGoodsIssue WHERE TrfNo = a.TrfNo)");

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
