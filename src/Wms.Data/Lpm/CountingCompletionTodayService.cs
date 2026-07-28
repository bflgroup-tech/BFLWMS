using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Counting Completion Report — "Today" mode. Unlike the regular Summary/
/// Allocation-wise/Detailed views (which read the nightly-batched
/// BFLDATA.dbo.BuildingCompletionSumm/Det tables), Today mode reads live,
/// same-day data for containers whose BFLDATA.dbo.BuildingCompletion.Trndate
/// is today (GST calendar day) — the nightly batch hasn't consolidated
/// today's counting into BuildingCompletionSumm/Det yet. Per-item detail
/// (UPC/Qty/PalletType/LPMDt/OraPoNo) comes from USA.dbo.UPCBoxDet/UPCBoxHead
/// and Division from hodata.dbo.vUSAOrder + USA.dbo.USAPriority — see
/// RawQuerySql for why (originally read Online.dbo.PhotoCheckingResult
/// directly, which scaled very badly over the course of a day).
///
/// Each selected country is queried on its own connection: UAE via the
/// existing OnPremBackupDB connection, every other country via its own
/// {Country}_DB_ConnectionString (assumed to mirror the same Online/BFLDATA
/// schema) — countries with no connection string configured are skipped
/// silently, same fallback behaviour as GetProductionCheckingAsync in
/// ReportsService.
///
/// Brand (USA.dbo.UPCBarCodes.Vendor) and Box Category TypeName
/// (BFLDATA.dbo.PalletType.TypeName, keyed by the PalletType code from
/// UPCBoxHead, e.g. 'KS' -> "EX2KSA") are both central-only master data
/// (UAE OnPremBackupDB), so they're looked up once for every distinct
/// UPC/ResultType across all countries' raw rows and joined in memory —
/// simpler and safer than bulk-copying every country's rows into a shared
/// temp table for a same-day data volume this small.
///
/// Connection reuse: opening a fresh connection to the on-prem server has
/// real latency from Azure App Service (TCP+TLS+login), so UAE's raw fetch
/// and the enrichment queries all share ONE OnPremBackupDB connection
/// (opened once per report call) instead of one connection per query, and
/// the two enrichment lookups run as a single QueryMultipleAsync batch
/// instead of two round trips.
/// </summary>
public class CountingCompletionTodayService(
    IOnPremConnectionResolver resolver, ILogger<CountingCompletionTodayService> logger)
{
    private const int CommandTimeoutSeconds = 120;

    // Sourced from BFLDATA.dbo.BuildingCompletion (today's completed containers) joined
    // to:
    //   - USA.dbo.UPCBoxDet (BoxNo is ContNo + '-' + sequence) for per-item UPC/Qty —
    //     the base table, not the vUPCBoxDet view, which unions in dozens of old
    //     archive/backup tables with no index for this BoxNo-prefix lookup and took
    //     30s+ in testing; the base table has IX_BoxNo and returned identical data
    //     in well under a second.
    //   - USA.dbo.UPCBoxHead (same BoxNo) for PalletType (Box Category) and also
    //     LPMDt/OraPoNo.
    //   - hodata.dbo.vUSAOrder JOINED BY REFNO (not Contno — vUSAOrder.Contno is an
    //     unrelated shipping/invoice reference; Contno = ContNo lives in Refno,
    //     confirmed against real data) for GroupCode, then USA.dbo.USAPriority (by
    //     GroupCode) for DivisionY. A container can map to several GroupCodes/
    //     Divisions, so this is pre-aggregated into #TodayDivs (STRING_AGG, comma-
    //     joined) rather than a single TOP-1 value. May be blank for containers too
    //     new to have an order record yet.
    // Originally sourced from Online.dbo.PhotoCheckingResult instead, but that scaled
    // very badly as the day's data grew (16-30s once a few thousand rows existed for
    // today, vs ~450ms end-to-end with this UPCBoxDet/UPCBoxHead-based version at the
    // same data volume) — switched over entirely rather than keeping it as a "fast
    // path" for already-checked containers.
    // ItemName isn't available from this source, so it's always blank now.
    // UPCBoxHead.WHouse holds a processing-stage value (e.g. "Sorting"), not JAFZA/
    // TECHNO, so it isn't used for the Warehouse column — that's still resolved via
    // Online.dbo.Photochecking in LookupWarehouseAsync, independent of this query.
    private const string RawQuerySql = @"
        IF OBJECT_ID('tempdb..#TodayConts') IS NOT NULL DROP TABLE #TodayConts;
        IF OBJECT_ID('tempdb..#TodayDivs')  IS NOT NULL DROP TABLE #TodayDivs;

        SELECT DISTINCT bc.ContNo
          INTO #TodayConts
          FROM BFLDATA.dbo.BuildingCompletion bc WITH (NOLOCK)
         WHERE CAST(bc.Trndate AS DATE) = @today;
        CREATE UNIQUE CLUSTERED INDEX IX_TodayConts ON #TodayConts(ContNo);

        SELECT ContNo, Divisions = STRING_AGG(DivisionY, ', ')
          INTO #TodayDivs
          FROM (SELECT DISTINCT o.Refno AS ContNo, p.DivisionY
                  FROM hodata.dbo.vUSAOrder o WITH (NOLOCK)
                  JOIN #TodayConts tc ON tc.ContNo = o.Refno
                  JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.groupCode = o.GroupCode
                 WHERE p.DivisionY IS NOT NULL AND p.DivisionY <> '') x
         GROUP BY ContNo;
        CREATE CLUSTERED INDEX IX_TodayDivs ON #TodayDivs(ContNo);

        SELECT bc.ContNo,
               ISNULL(v.UPC, '') AS UPC,
               CAST(NULL AS VARCHAR(500)) AS ItemName,
               td.Divisions AS Division,
               h.PalletType AS ResultType,
               ISNULL(v.Qty, 0) AS QtyIssue,
               h.LPMDt AS LPMDt,
               h.OraPoNo AS ORAPONo
          FROM #TodayConts bc
          LEFT JOIN USA.dbo.UPCBoxDet v WITH (NOLOCK) ON v.BoxNo LIKE bc.ContNo + '-%'
          LEFT JOIN USA.dbo.UPCBoxHead h WITH (NOLOCK) ON h.BoxNo = v.BoxNo
          LEFT JOIN #TodayDivs td ON td.ContNo = bc.ContNo;

        DROP TABLE #TodayConts, #TodayDivs;";

    private record RawRow(string ContNo, string UPC, string? ItemName, string? Division,
        string? ResultType, int QtyIssue, DateTime? LPMDt, string? ORAPONo);

    private record CountryRow(string Country, RawRow Row);

    private async Task<List<CountryRow>> FetchRawAsync(
        IReadOnlyList<string> countries, SqlConnection onPremBackup, CancellationToken ct)
    {
        var today = DateTime.UtcNow.AddHours(4).Date;
        var all = new List<CountryRow>();

        foreach (var country in countries)
        {
            // MALAYSIA's connection string is configured but its server doesn't have
            // the Online database this report needs — excluded for now until that's
            // sorted out, rather than surfacing a raw SQL error to the user.
            if (string.Equals(country, "MALAYSIA", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
            {
                // Reuse the shared connection — UAE's data and the enrichment lookups
                // both live on OnPremBackupDB, so there's no need for a second connection.
                var rows = await onPremBackup.QueryAsync<RawRow>(new CommandDefinition(
                    RawQuerySql, new { today }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                all.AddRange(rows.Select(r => new CountryRow(country, r)));
                continue;
            }

            string? connStr;
            try { connStr = resolver.GetCountryConnectionString(country); }
            catch { connStr = null; }
            if (string.IsNullOrWhiteSpace(connStr)) continue; // not configured — skip silently

            try
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync(ct);
                var rows = await conn.QueryAsync<RawRow>(new CommandDefinition(
                    RawQuerySql, new { today }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                all.AddRange(rows.Select(r => new CountryRow(country, r)));
            }
            catch
            {
                // That country's server is reachable but doesn't have what this query
                // needs (e.g. no Online database) — skip it rather than failing the
                // whole report for every other selected country.
            }
        }
        return all;
    }

    private async Task<(Dictionary<string, string?> brandByUpc, Dictionary<string, string?> typeNameByResultType,
        Dictionary<string, string?> itemNameByUpc, Dictionary<string, string?> divisionByUpc)>
        EnrichAsync(IReadOnlyList<CountryRow> raw, SqlConnection onPremBackup, CancellationToken ct)
    {
        var brandByUpc = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var typeNameByResultType = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var itemNameByUpc = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var divisionByUpc = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var upcs = raw.Select(r => r.Row.UPC).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToArray();
        var resultTypes = raw.Select(r => r.Row.ResultType).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToArray();
        if (upcs.Length == 0 && resultTypes.Length == 0)
            return (brandByUpc, typeNameByResultType, itemNameByUpc, divisionByUpc);

        // UPC IN @upcs as a Dapper array expands into one SQL parameter per UPC — with a
        // few thousand distinct UPCs (typical for a busy counting day) SQL Server's plan
        // compilation for that many ad-hoc parameters degrades from milliseconds to minutes.
        // Passing the list as a single CSV string and splitting it into an indexed temp
        // table server-side avoids that entirely (verified: ~1600 UPCs went from a 60s+
        // hang to ~13ms).
        var upcsCsv = string.Join(",", upcs);

        // Single round trip for all lookups instead of separate QueryAsync calls.
        // ItemName and per-item Division (via GroupCode -> USAPriority.DivisionY) come
        // from the same UPCBarCodes row as Brand (Vendor) — this is item-level (one
        // Division per UPC), unlike the container-level comma-joined Divisions used by
        // Summary/Allocation-wise, so the Detailed/Item-wise view shows just the one
        // division relevant to that specific item.
        await using var multi = await onPremBackup.QueryMultipleAsync(new CommandDefinition(@"
            SELECT CAST(value AS VARCHAR(50)) AS UPC INTO #upcs FROM STRING_SPLIT(@upcsCsv, ',');
            CREATE UNIQUE CLUSTERED INDEX IX_upcs_tmp ON #upcs(UPC);

            SELECT b.UPC, Vendor = MAX(b.Vendor), ItemName = MAX(b.ItemName), Division = MAX(p.DivisionY)
              FROM USA.dbo.UPCBarCodes b WITH (NOLOCK)
              INNER JOIN #upcs u ON u.UPC = b.UPC
              LEFT JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.groupCode = b.GroupCode
             GROUP BY b.UPC;

            SELECT PalletType, TypeName
              FROM BFLDATA.dbo.PalletType WITH (NOLOCK)
             WHERE @hasTypes = 1 AND PalletType IN @resultTypes;",
            new
            {
                upcsCsv,
                hasTypes = resultTypes.Length > 0 ? 1 : 0,
                resultTypes = resultTypes.Length > 0 ? resultTypes : new[] { "" }
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var brandRows = await multi.ReadAsync<(string UPC, string? Vendor, string? ItemName, string? Division)>();
        foreach (var r in brandRows)
        {
            brandByUpc[r.UPC] = r.Vendor;
            itemNameByUpc[r.UPC] = r.ItemName;
            divisionByUpc[r.UPC] = r.Division;
        }

        var typeRows = await multi.ReadAsync<(string PalletType, string? TypeName)>();
        foreach (var r in typeRows) typeNameByResultType[r.PalletType] = r.TypeName;

        return (brandByUpc, typeNameByResultType, itemNameByUpc, divisionByUpc);
    }

    private async Task<Dictionary<string, string?>> LookupWarehouseAsync(
        IReadOnlyList<CountryRow> raw, SqlConnection onPremBackup, CancellationToken ct)
    {
        var warehouseByContNo = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var uaeContNos = raw
            .Where(r => string.Equals(r.Country, "UAE", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Row.ContNo).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (uaeContNos.Length == 0) return warehouseByContNo;

        // Same CSV + indexed-temp-table pattern as the UPC enrichment fix (avoids a
        // large Dapper array parameter list), and the same TOP 1/APPLY lookup as the
        // regular (non-Today) Counting Completion views — a single container can have
        // 100k+ rows in Online.dbo.Photochecking, so aggregating (MAX/DISTINCT) over
        // every matching row is much slower than stopping at the first match via
        // FORCESEEK.
        var contNosCsv = string.Join(",", uaeContNos);
        var rows = await onPremBackup.QueryAsync<(string ContNo, string? Warehouse)>(new CommandDefinition(@"
            SELECT CAST(value AS VARCHAR(50)) AS ContNo INTO #whContNos FROM STRING_SPLIT(@contNosCsv, ',');
            CREATE UNIQUE CLUSTERED INDEX IX_whContNos_tmp ON #whContNos(ContNo);

            SELECT b.ContNo, wh.Warehouse
              FROM #whContNos b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;",
            new { contNosCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // Containers too new to have hit Online.dbo.Photochecking yet (same sync lag
        // as the BuildingCompletion fallback branch above) default to JAFZA rather
        // than showing blank.
        foreach (var r in rows) warehouseByContNo[r.ContNo] = r.Warehouse ?? "JAFZA";
        return warehouseByContNo;
    }

    private async Task<(List<CountryRow> raw, Dictionary<string, string?> brandByUpc, Dictionary<string, string?> typeNameByResultType,
        Dictionary<string, string?> itemNameByUpc, Dictionary<string, string?> divisionByUpc, Dictionary<string, string?> warehouseByContNo)>
        FetchAndEnrichAsync(IReadOnlyList<string> countries, string? warehouseFilter, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var onPremBackup = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        await onPremBackup.OpenAsync(ct);
        var openMs = sw.ElapsedMilliseconds; sw.Restart();

        var raw = await FetchRawAsync(countries, onPremBackup, ct);
        var fetchMs = sw.ElapsedMilliseconds; sw.Restart();

        var (brandByUpc, typeNameByResultType, itemNameByUpc, divisionByUpc) = await EnrichAsync(raw, onPremBackup, ct);
        var enrichMs = sw.ElapsedMilliseconds; sw.Restart();

        var warehouseByContNo = await LookupWarehouseAsync(raw, onPremBackup, ct);
        var whMs = sw.ElapsedMilliseconds;

        if (!string.IsNullOrWhiteSpace(warehouseFilter))
        {
            raw = raw.Where(r =>
                !string.Equals(r.Country, "UAE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(warehouseByContNo.GetValueOrDefault(r.Row.ContNo), warehouseFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        logger.LogInformation(
            "CountingCompletionToday: connect={OpenMs}ms fetch={FetchMs}ms ({RowCount} rows, countries={Countries}) enrich={EnrichMs}ms warehouse={WhMs}ms",
            openMs, fetchMs, raw.Count, string.Join(",", countries), enrichMs, whMs);

        return (raw, brandByUpc, typeNameByResultType, itemNameByUpc, divisionByUpc, warehouseByContNo);
    }

    private static string? CommaJoin(IEnumerable<string?> values) =>
        string.Join(", ", values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

    public async Task<List<CountingCompletionTodaySummaryRow>> GetSummaryAsync(
        IReadOnlyList<string> countries, string? warehouse = null, CancellationToken ct = default)
    {
        var (raw, brandByUpc, typeNameByResultType, _, _, warehouseByContNo) = await FetchAndEnrichAsync(countries, warehouse, ct);

        return raw
            .GroupBy(r => (r.Country, r.Row.ContNo))
            .Select(g => new CountingCompletionTodaySummaryRow(
                Country:     g.Key.Country,
                ContNo:      g.Key.ContNo,
                CountedQty:  g.Sum(x => x.Row.QtyIssue),
                LpmDates:    CommaJoin(g.Select(x => x.Row.LPMDt).Where(d => d.HasValue).Select(d => d!.Value.ToString("MMM-yyyy"))),
                Divisions:   CommaJoin(g.Select(x => x.Row.Division)),
                OraPoNos:    CommaJoin(g.Select(x => x.Row.ORAPONo)),
                Brands:      CommaJoin(g.Select(x => brandByUpc.GetValueOrDefault(x.Row.UPC))),
                PalletTypes: CommaJoin(g.Select(x => x.Row.ResultType)),
                TypeNames:   CommaJoin(g.Select(x => x.Row.ResultType).Where(rt => !string.IsNullOrWhiteSpace(rt)).Select(rt => typeNameByResultType.GetValueOrDefault(rt!))),
                Warehouse:   warehouseByContNo.GetValueOrDefault(g.Key.ContNo)))
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }

    public async Task<List<CountingCompletionTodayAllocationRow>> GetAllocationAsync(
        IReadOnlyList<string> countries, string? warehouse = null, CancellationToken ct = default)
    {
        var (raw, brandByUpc, typeNameByResultType, _, _, warehouseByContNo) = await FetchAndEnrichAsync(countries, warehouse, ct);

        return raw
            .GroupBy(r => (r.Country, r.Row.ContNo, ResultType: r.Row.ResultType ?? "(none)"))
            .Select(g => new CountingCompletionTodayAllocationRow(
                Country:    g.Key.Country,
                ContNo:     g.Key.ContNo,
                ResultType: g.Key.ResultType,
                TypeName:   typeNameByResultType.GetValueOrDefault(g.Key.ResultType),
                BuildQty:   g.Sum(x => x.Row.QtyIssue),
                LpmDates:   CommaJoin(g.Select(x => x.Row.LPMDt).Where(d => d.HasValue).Select(d => d!.Value.ToString("MMM-yyyy"))),
                Divisions:  CommaJoin(g.Select(x => x.Row.Division)),
                OraPoNos:   CommaJoin(g.Select(x => x.Row.ORAPONo)),
                Brands:     CommaJoin(g.Select(x => brandByUpc.GetValueOrDefault(x.Row.UPC))),
                Warehouse:  warehouseByContNo.GetValueOrDefault(g.Key.ContNo)))
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }

    public async Task<List<CountingCompletionTodayDetailRow>> GetDetailAsync(
        IReadOnlyList<string> countries, string? warehouse = null, CancellationToken ct = default)
    {
        var (raw, brandByUpc, typeNameByResultType, itemNameByUpc, divisionByUpc, warehouseByContNo) = await FetchAndEnrichAsync(countries, warehouse, ct);

        return raw
            .GroupBy(r => (Country: r.Country, ContNo: r.Row.ContNo, UPC: r.Row.UPC))
            .Select(g =>
            {
                var palletType = g.Select(x => x.Row.ResultType).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
                return new CountingCompletionTodayDetailRow(
                    Country:    g.Key.Country,
                    ContNo:     g.Key.ContNo,
                    ItemCode:   g.Key.UPC,
                    ItemName:   itemNameByUpc.GetValueOrDefault(g.Key.UPC),
                    Qty:        g.Sum(x => x.Row.QtyIssue),
                    LpmDt:      g.Select(x => x.Row.LPMDt).FirstOrDefault(d => d.HasValue),
                    Division:   divisionByUpc.GetValueOrDefault(g.Key.UPC),
                    OraPoNo:    g.Select(x => x.Row.ORAPONo).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    Brand:      brandByUpc.GetValueOrDefault(g.Key.UPC),
                    PalletType: palletType,
                    TypeName:   palletType is null ? null : typeNameByResultType.GetValueOrDefault(palletType),
                    Warehouse:  warehouseByContNo.GetValueOrDefault(g.Key.ContNo));
            })
            .OrderBy(r => r.Country).ThenBy(r => r.ContNo)
            .ToList();
    }
}
