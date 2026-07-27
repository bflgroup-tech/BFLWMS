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
            // UAE stores each have their own linked-server DataName on OnPremBackup.
            // GRNHeaderRF/TransferReverse live in those linked-server DBs, not in
            // BFLDATA.dbo. Resolve all UAE DataNames then query each via linked server.
            await using var onprem = OpenOnPrem();
            var uaeDataNames = (await onprem.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT DataName
                  FROM BFLDATA.dbo.DataSettings
                 WHERE SIMCountry = 'UAE'
                   AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

            var all = new List<TransferHistoryRow>();
            foreach (var dn in uaeDataNames)
            {
                var sql  = BuildSqlOnPrem(dn, f, out var parms, simCountry: UaeCountry);
                var rows = await onprem.QueryAsync<TransferHistoryRow>(
                    new CommandDefinition(sql, parms,
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                all.AddRange(rows);
            }
            return all;
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

    private record CountQtyRow(int TransferCount, int? TransferQty);

    // Per-country totals, scoped to Country + date range only (no Store/search/
    // without-flags — these are top-of-page KPI cards, not tied to the detail
    // table's extra filters). countries: null/empty = every SIM country.
    public async Task<List<TransferSummary>> GetTransferSummaryAsync(
        IReadOnlyCollection<string>? countries, DateTime dateFrom, DateTime dateTo, CancellationToken ct = default)
    {
        var list = countries is { Count: > 0 } ? countries.ToList() : await GetCountriesAsync(ct);
        var from = dateFrom.Date;
        var to   = dateTo.Date.AddDays(1).AddSeconds(-1);

        var tasks = list.Select(async country =>
        {
            try { return await GetSummaryForCountryAsync(country, from, to, ct); }
            catch { return new TransferSummary(country, 0, 0, 0); }
        });
        var results = await Task.WhenAll(tasks);
        return results.OrderBy(s => s.Country).ToList();
    }

    // dataNameTable e.g. "[EX2KSA]..vTransferDetail" (UAE linked server) or
    // "BFLDATA..vTransferDetail" (non-UAE country server's own sibling BFLDATA db).
    // CostCodeTo/LocCodeTo '005'/'05' exclude the warehouse's own internal transfers.
    private static string TransferSummarySql(string transferDetailTable) => $@"
        SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
          FROM {transferDetailTable} WITH (NOLOCK)
         WHERE TrfDate >= @from AND TrfDate <= @to
           AND CostCodeTo <> '005' AND LocCodeTo <> '05'";

    private async Task<TransferSummary> GetSummaryForCountryAsync(
        string country, DateTime from, DateTime to, CancellationToken ct)
    {
        if (country == UaeCountry)
        {
            await using var onprem = OpenOnPrem();
            var uaeDataNames = (await onprem.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT DataName
                  FROM BFLDATA.dbo.DataSettings
                 WHERE SIMCountry = 'UAE'
                   AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

            int transferCount = 0, transferQty = 0;
            foreach (var dn in uaeDataNames)
            {
                var row = await onprem.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                    TransferSummarySql($"[{dn}]..vTransferDetail"), new { from, to },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                transferCount += row.TransferCount;
                transferQty   += row.TransferQty ?? 0;
            }

            // GIN pallets for UAE live centrally in BFLDATA.dbo — scoped to UAE via
            // ShopIssue -> DataSettings.SIMCountry (same linkage ShipmentStatusService
            // uses), since vGoodsIssueplt itself has no country column.
            var ginQty = await onprem.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT ISNULL(SUM(c.Qty),0)
                  FROM BFLDATA.dbo.vGoodsIssueplt c WITH (NOLOCK)
                  JOIN BFLDATA.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = c.ShopIssue
                 WHERE ds.SIMCountry = 'UAE' AND c.EntryDate >= @from AND c.EntryDate <= @to",
                new { from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            return new TransferSummary(country, transferCount, transferQty, ginQty ?? 0);
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

            var transferRow = await conn.QuerySingleAsync<CountQtyRow>(new CommandDefinition(
                TransferSummarySql("BFLDATA..vTransferDetail"), new { from, to },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var ginQty = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                SELECT ISNULL(SUM(Qty),0)
                  FROM BFLDATA..vgoodsissueplt WITH (NOLOCK)
                 WHERE EntryDate >= @from AND EntryDate <= @to",
                new { from, to }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            return new TransferSummary(country, transferRow.TransferCount, transferRow.TransferQty ?? 0, ginQty ?? 0);
        }
    }

    // ── SQL builders ─────────────────────────────────────────────────────────

    // OnPremBackup path: tables accessed via linked-server prefix [{dataName}].
    // Pass simCountry (e.g. "UAE") to add a SIMCountry filter on DataSettings.
    private static string BuildSqlOnPrem(
        string dataName, TransferHistoryFilter f, out DynamicParameters p,
        string? simCountry = null)
    {
        p = new DynamicParameters();
        var hasSearch    = !string.IsNullOrWhiteSpace(f.SearchValue);
        var simFilter    = simCountry is null ? "" : $"\n   AND e.SIMCountry = '{simCountry}'";
        var dateFilter   = hasSearch ? "" : "\n   AND a.TrfDate >= @from AND a.TrfDate <= @to";
        if (!hasSearch) { p.Add("@from", f.DateFrom.Date); p.Add("@to", f.DateTo.Date.AddDays(1).AddSeconds(-1)); }

        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfDate, a.TrfNo, c.SrNo) SrNo,
       e.ShopName,
       a.TrfNo,
       a.TrfDate,
       PalletNo  = (SELECT TOP 1 PalletNo FROM BFLDATA.dbo.vGoodsIssue WHERE TrfNo = a.TrfNo ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM [{dataName}]..transferheader           a
  LEFT JOIN BFLDATA.dbo.vGoodsIssue           b  ON b.TrfNo = a.TrfNo
  LEFT JOIN BFLDATA.dbo.vGoodsIssueplt        c  ON c.TrfNo = a.TrfNo
  LEFT JOIN [{dataName}]..GRNHeaderRF          d  ON d.TrfNo = a.TrfNo
  JOIN  BFLDATA.dbo.DataSettings              e  ON a.CostCodeTo = e.CostCodeTo
  LEFT JOIN [{dataName}]..TransferReverse      f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}{simFilter}
   AND e.ShopName NOT IN (
       SELECT ShopName FROM BFLDATA.dbo.DataSettings WHERE Concept = 'Warehouse'
   )");

        AppendCommonFilters(sb, p, f);
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
       PalletNo  = (SELECT TOP 1 PalletNo FROM BFLDATA..vGoodsIssue WHERE TrfNo = a.TrfNo ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM transferheader              a
  LEFT JOIN BFLDATA..vGoodsIssue   b  ON b.TrfNo = a.TrfNo
  LEFT JOIN BFLDATA..vGoodsIssueplt c  ON c.TrfNo = a.TrfNo
  LEFT JOIN GRNHeaderRF             d  ON d.TrfNo = a.TrfNo
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

    private static void AppendCommonFilters(StringBuilder sb, DynamicParameters p, TransferHistoryFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.Store))
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
