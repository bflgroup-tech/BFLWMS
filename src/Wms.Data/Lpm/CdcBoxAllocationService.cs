using System.Data;
using System.Globalization;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// CDC Box Allocation — step 1: spread the UAE DC eligible SOH across stores.
///
/// This is the "Allocate DC SOH to Stores" button. It takes the eligible DC
/// stock for a chosen set of LPM months, decides how much of each SKU each
/// store should hold (Volume Group + LPM_SkuMaxBands tier, exactly the rule PO
/// allocation uses), and writes the result to LPMSIM.dbo.DC_STORE_SOH_ALLOCATION.
///
/// Step 2 (the Process button) then runs the PO allocation logic with that table
/// standing in for store SOH, and enforces box-to-country integrity. Splitting it
/// in two is deliberate: the table is checkable against the business's own
/// numbers before the harder half depends on it.
///
/// Differences from PO allocation's tier pick, and why:
///   - The band basis is the SKU's TOTAL eligible DC qty, not a PO line qty —
///     there is no PO here, the DC pool is what is being distributed.
///   - OTS% is the static OtsPercentToday, not a running figure. Nothing is
///     consumed at this stage, so there is no allocation to decrement OTS by;
///     the running-OTS refresh belongs to the Process step.
///   - OTS comes from WmsOtsCdcAllocationRun (the CDC OTS page), which is the
///     same calculation as the PO one with UAE DC SOH forced to zero — correct
///     here, since the UAE DC stock is precisely what we are handing out.
///
/// Connections: OnPremBackupDB for everything (racks, datareporting, LPMSIM);
/// Azure WMS only for the OTSBandPct knob and the country list.
/// </summary>
public class CdcBoxAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;

    /// <summary>Band width in percentage points either side of AvgOTS. Same knob PO allocation reads.</summary>
    private const double DefaultOtsBandPct = 10.0;

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

    private static DateTime NowGst() => DateTime.UtcNow.AddHours(4);

    /// <summary>"Mar-2026" — how an LPM month is labelled everywhere else in the app.</summary>
    public static string LpmLabel(DateTime d) => d.ToString("MMM-yyyy", CultureInfo.InvariantCulture);

    // ===================== Inputs =====================

    /// <summary>
    /// The LPM months that actually have eligible DC stock behind them, with box
    /// and qty counts so the picker shows what is being selected rather than a
    /// bare list of dates. Eligible = PalletCategory 'Eligible' AND ShopEligible
    /// not 'E' (the shop-eligible flag marks stock already earmarked for a shop).
    /// </summary>
    public async Task<List<CdcLpmOption>> GetEligibleLpmsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(DateTime LpmDt, int Boxes, int Items, long Qty)>(new CommandDefinition(@"
            SELECT LpmDt = CAST(b.LPMDt AS date),
                   Boxes = COUNT(DISTINCT b.BoxNo),
                   Items = COUNT(DISTINCT b.ItemCode),
                   Qty   = SUM(CAST(ISNULL(b.Qty, 0) AS bigint))
              FROM racks.dbo.WHBoxItems b WITH (NOLOCK)
             WHERE b.PalletCategory = 'Eligible'
               AND ISNULL(b.ShopEligible, '') <> 'E'
               AND b.LPMDt IS NOT NULL
             GROUP BY CAST(b.LPMDt AS date)
            HAVING SUM(CAST(ISNULL(b.Qty, 0) AS bigint)) > 0
             ORDER BY 1 DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows.Select(r => new CdcLpmOption(r.LpmDt, LpmLabel(r.LpmDt), r.Boxes, r.Items, r.Qty)).ToList();
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM dbo.WmsWHMaster WHERE Active = 1 ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    // ===================== Allocate DC SOH to Stores =====================

    /// <summary>
    /// Distribute the eligible DC SOH of the selected LPM months across every
    /// store of the selected countries, and persist to DC_STORE_SOH_ALLOCATION.
    /// Replaces whatever the table held — one run at a time, by design.
    /// </summary>
    public async Task<CdcDcSohAllocationResult> AllocateDcSohToStoresAsync(
        IReadOnlyList<DateTime> lpmDates,
        IReadOnlyList<string> countries,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (lpmDates is null || lpmDates.Count == 0)
            return CdcDcSohAllocationResult.Fail("Pick at least one LPM.");
        if (countries is null || countries.Count == 0)
            return CdcDcSohAllocationResult.Fail("Pick at least one allocation country.");

        var dates = lpmDates.Select(d => d.Date).Distinct().OrderBy(d => d).ToArray();
        var ctry  = countries.Select(x => x.Trim()).Where(x => x.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var nowGst = NowGst();

        // ---------- 1. The DC pool being handed out ----------
        progress?.Report("Reading eligible DC stock…");
        Dictionary<string, int> dcQtyByItem;
        await using (var c = OpenOnPremBackup())
        {
            var rows = await c.QueryAsync<(string ItemCode, int Qty)>(new CommandDefinition(@"
                SELECT b.ItemCode, Qty = SUM(CAST(ISNULL(b.Qty, 0) AS int))
                  FROM racks.dbo.WHBoxItems b WITH (NOLOCK)
                 WHERE b.PalletCategory = 'Eligible'
                   AND ISNULL(b.ShopEligible, '') <> 'E'
                   AND CAST(b.LPMDt AS date) IN @dates
                   AND b.ItemCode IS NOT NULL
                 GROUP BY b.ItemCode
                HAVING SUM(CAST(ISNULL(b.Qty, 0) AS int)) > 0",
                new { dates },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            dcQtyByItem = rows.ToDictionary(r => r.ItemCode.Trim().ToUpperInvariant(), r => r.Qty);
        }

        if (dcQtyByItem.Count == 0)
            return CdcDcSohAllocationResult.Fail(
                $"No eligible DC stock for {string.Join(", ", dates.Select(LpmLabel))}.");

        // CSV + STRING_SPLIT rather than an IN-list: item counts run into the
        // thousands and SQL Server caps a statement at 2100 parameters.
        var itemCodesCsv = string.Join(",", dcQtyByItem.Keys);

        // ---------- 2. Reference data (fan out — all independent) ----------
        progress?.Report("Loading divisions, OTS, grades and SKU Max bands…");

        var tDiv       = LoadDivByItemAsync(itemCodesCsv, ct);
        var tOts       = LoadOtsRunRowsAsync(ctry, nowGst, ct);
        var tGrade     = LoadStoreDivGradeAsync(nowGst, ct);
        var tSoh       = LoadItemSohByStoreAsync(itemCodesCsv, ct);
        var tVgOrder   = LoadVolumeGroupOrderAsync(ct);
        var tBandPct   = LoadOtsBandPctAsync(ct);

        await Task.WhenAll(tDiv, tOts, tGrade, tSoh, tVgOrder, tBandPct);

        var divByItem      = await tDiv;
        var otsRows        = await tOts;
        var storeDivGrade  = await tGrade;
        var itemSohByStore = await tSoh;
        var vgSortOrder    = await tVgOrder;
        var otsBandPct     = await tBandPct;

        if (otsRows.Count == 0)
            return CdcDcSohAllocationResult.Fail(
                $"No OTS rows in WmsOtsCdcAllocationRun for {nowGst:dd-MMM-yyyy} and " +
                $"{string.Join(", ", ctry)}. Generate OTS on the \"OTS for CDC Box Allocation\" page first.");

        // Overlay this month's grade; a store with no grade cannot be tiered.
        foreach (var r in otsRows)
            r.VolumeGroup = storeDivGrade.TryGetValue((r.StoreID, r.DivCode), out var g) ? g : null;

        var storesByDiv = otsRows.GroupBy(r => r.DivCode)
                                 .ToDictionary(g => g.Key, g => g.ToList());

        var distinctDivs = divByItem.Values.Where(d => d > 0).Distinct().ToArray();
        var bandsByKey   = await LoadSkuMaxBandsAsync(distinctDivs, ct);

        // ---------- 3. Allocate ----------
        progress?.Report($"Allocating {dcQtyByItem.Count:N0} SKUs across {otsRows.Count:N0} store/division rows…");

        // Z is the first grade, ahead of A — same hard-coding as PO allocation,
        // because Z has no LPM_VolumeGroupRange row and would otherwise sort last.
        int VolumeGroupRank(string? vg)
        {
            if (string.IsNullOrWhiteSpace(vg)) return 999;
            if (char.ToUpperInvariant(vg.Trim()[0]) == 'Z') return -1;
            return vgSortOrder.TryGetValue(vg.Trim(), out var rank) ? rank : 999;
        }

        var runTs        = nowGst;
        var runBy        = user.Name;
        var lpmScope     = string.Join(", ", dates.Select(LpmLabel));
        var countryScope = string.Join(", ", ctry);

        var outRows       = new List<CdcDcSohAllocationRow>();
        var noDivision    = 0;
        var noStores      = 0;
        var noBand        = 0;
        long totalDcQty   = 0;
        long totalAlloc   = 0;

        foreach (var (item, dcQty) in dcQtyByItem)
        {
            totalDcQty += dcQty;

            if (!divByItem.TryGetValue(item, out var div) || div <= 0) { noDivision++; continue; }
            if (!storesByDiv.TryGetValue(div, out var divStores) || divStores.Count == 0) { noStores++; continue; }

            // Only stores that can actually be tiered: a grade AND a band covering
            // this SKU's DC qty. Stores failing either are simply not candidates —
            // the same condition PO allocation reports as Blocked.
            var eligible = new List<(CdcOtsStoreRow Row, (int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax) Band)>();
            foreach (var r in divStores)
            {
                if (string.IsNullOrWhiteSpace(r.VolumeGroup)) continue;
                if (!bandsByKey.TryGetValue((div, r.VolumeGroup!), out var bands)) continue;
                foreach (var b in bands)
                    if (dcQty >= b.From && dcQty <= b.To) { eligible.Add((r, b)); break; }
            }
            if (eligible.Count == 0) { noBand++; continue; }

            // AvgOTS over positive OTS% within this division — the tier threshold.
            var positives = eligible.Select(e => (double)e.Row.OtsPercentToday).Where(p => p > 0).ToList();
            var avgOts = positives.Count > 0 ? positives.Average() : 0.0;
            var avgOtsDecimal = (decimal)Math.Round(avgOts, 2);

            var ranked = eligible
                .OrderBy(e => VolumeGroupRank(e.Row.VolumeGroup))
                .ThenByDescending(e => e.Row.OtsPercentToday)
                .ToList();

            var remaining = dcQty;
            foreach (var (r, b) in ranked)
            {
                if (remaining <= 0) break;

                var ots = (double)r.OtsPercentToday;
                var (tierValue, tierName) = ots switch
                {
                    < 0                               => (b.MinMin,   "MinMin"),
                    _ when ots <  avgOts - otsBandPct => (b.MinMax,   "MinMax"),
                    _ when ots <= avgOts + otsBandPct => (b.IdealMax, "IdealMax"),
                    _                                 => (b.MaxMax,   "MaxMax"),
                };
                var tier = tierValue ?? 0;
                var soh  = itemSohByStore.GetValueOrDefault((r.StoreID.ToUpperInvariant(), item), 0);
                var cap  = Math.Max(0, tier - soh);
                if (cap <= 0) continue;

                var take = Math.Min(cap, remaining);
                remaining -= take;
                totalAlloc += take;

                outRows.Add(new CdcDcSohAllocationRow(
                    Itemcode: item, DivCode: div, Country: r.Country, StoreID: r.StoreID,
                    VolumeGroup: r.VolumeGroup, DcQty: dcQty,
                    OtsPercent: r.OtsPercentToday, AvgOtsPercent: avgOtsDecimal,
                    TierName: tierName, SkuMaxTier: tier, StoreSoh: soh, AllocatedSoh: take));
            }
        }

        if (noDivision > 0) warnings.Add($"{noDivision:N0} SKU(s) skipped — no DivID in datareporting..vupc_subclass.");
        if (noStores   > 0) warnings.Add($"{noStores:N0} SKU(s) skipped — no OTS store rows for their division in the selected countries.");
        if (noBand     > 0) warnings.Add($"{noBand:N0} SKU(s) skipped — no LPM_SkuMaxBands (BFLGROUP) row covering their DC qty for any graded store.");

        var unallocated = totalDcQty - totalAlloc;
        if (unallocated > 0)
            warnings.Add($"{unallocated:N0} unit(s) of DC stock could not be placed — every candidate store was already at or above its SKU Max tier.");

        // ---------- 4. Persist ----------
        progress?.Report($"Writing {outRows.Count:N0} store rows…");
        await PersistAsync(outRows, runTs, runBy, lpmScope, countryScope, ct);

        return new CdcDcSohAllocationResult(
            Success: true, Message: null,
            RunTS: runTs, RunBy: runBy, LpmScope: lpmScope, CountryScope: countryScope,
            SkuCount: dcQtyByItem.Count,
            StoreRowCount: outRows.Count,
            TotalDcQty: totalDcQty,
            TotalAllocated: totalAlloc,
            Unallocated: Math.Max(0, unallocated),
            Warnings: warnings);
    }

    private async Task PersistAsync(
        List<CdcDcSohAllocationRow> rows, DateTime runTs, string? runBy,
        string lpmScope, string countryScope, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("RunTS",         typeof(DateTime));
        dt.Columns.Add("RunBy",         typeof(string));
        dt.Columns.Add("LpmScope",      typeof(string));
        dt.Columns.Add("CountryScope",  typeof(string));
        dt.Columns.Add("Itemcode",      typeof(string));
        dt.Columns.Add("DivCode",       typeof(int));
        dt.Columns.Add("Country",       typeof(string));
        dt.Columns.Add("StoreID",       typeof(string));
        dt.Columns.Add("VolumeGroup",   typeof(string));
        dt.Columns.Add("DcQty",         typeof(int));
        dt.Columns.Add("OtsPercent",    typeof(decimal));
        dt.Columns.Add("AvgOtsPercent", typeof(decimal));
        dt.Columns.Add("TierName",      typeof(string));
        dt.Columns.Add("SkuMaxTier",    typeof(int));
        dt.Columns.Add("StoreSoh",      typeof(int));
        dt.Columns.Add("AllocatedSoh",  typeof(int));

        foreach (var r in rows)
            dt.Rows.Add(runTs, (object?)runBy ?? DBNull.Value, lpmScope, countryScope,
                        r.Itemcode, r.DivCode, r.Country, r.StoreID,
                        (object?)r.VolumeGroup ?? DBNull.Value, r.DcQty,
                        r.OtsPercent, r.AvgOtsPercent, r.TierName,
                        r.SkuMaxTier, r.StoreSoh, r.AllocatedSoh);

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);

        // The table carries one run. DELETE (not TRUNCATE) so it works without
        // ALTER permission and stays inside the transaction with the insert.
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.DC_STORE_SOH_ALLOCATION", transaction: tx,
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        if (dt.Rows.Count > 0)
        {
            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.DC_STORE_SOH_ALLOCATION",
                BatchSize = 5000,
                BulkCopyTimeout = CommandTimeoutSeconds,
            };
            foreach (DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }

        await tx.CommitAsync(ct);
    }

    // ===================== Read back =====================

    /// <summary>Header of whatever run currently populates the table; null when empty.</summary>
    public async Task<CdcDcSohAllocationHeader?> GetLastRunAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var row = await c.QueryFirstOrDefaultAsync<(DateTime? RunTS, string? RunBy, string? LpmScope, string? CountryScope, int Rows, long Qty)>(
            new CommandDefinition(@"
                SELECT TOP 1 RunTS, RunBy, LpmScope, CountryScope,
                       Rows = COUNT(*) OVER (),
                       Qty  = SUM(CAST(AllocatedSoh AS bigint)) OVER ()
                  FROM dbo.DC_STORE_SOH_ALLOCATION WITH (NOLOCK)",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return row.RunTS is null
            ? null
            : new CdcDcSohAllocationHeader(row.RunTS.Value, row.RunBy, row.LpmScope, row.CountryScope, row.Rows, row.Qty);
    }

    /// <summary>
    /// Grid rows for the page. Capped because a full run is one row per
    /// (SKU, store) and can reach six figures — the page is for spot-checking,
    /// the export is for the whole thing.
    /// </summary>
    public async Task<List<CdcDcSohAllocationRow>> LoadAllocationAsync(
        string? itemFilter = null, string? storeFilter = null, int top = 5000, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcDcSohAllocationRow>(new CommandDefinition($@"
            SELECT TOP (@top)
                   Itemcode, DivCode, Country, StoreID, VolumeGroup, DcQty,
                   OtsPercent, AvgOtsPercent, TierName, SkuMaxTier, StoreSoh, AllocatedSoh
              FROM dbo.DC_STORE_SOH_ALLOCATION WITH (NOLOCK)
             WHERE (@item  IS NULL OR Itemcode LIKE '%' + @item  + '%')
               AND (@store IS NULL OR StoreID  LIKE '%' + @store + '%')
             ORDER BY Itemcode, Country, StoreID",
            new
            {
                top,
                item  = string.IsNullOrWhiteSpace(itemFilter)  ? null : itemFilter.Trim(),
                store = string.IsNullOrWhiteSpace(storeFilter) ? null : storeFilter.Trim(),
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Per-country roll-up — the quickest sanity check that the spread looks right.</summary>
    public async Task<List<CdcDcSohCountrySummaryRow>> LoadCountrySummaryAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcDcSohCountrySummaryRow>(new CommandDefinition(@"
            SELECT Country,
                   Stores    = COUNT(DISTINCT StoreID),
                   Skus      = COUNT(DISTINCT Itemcode),
                   Allocated = SUM(CAST(AllocatedSoh AS bigint))
              FROM dbo.DC_STORE_SOH_ALLOCATION WITH (NOLOCK)
             GROUP BY Country
             ORDER BY Allocated DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Step 2: Process (whole-box plan) =====================

    /// <summary>
    /// Turn the per-store DC targets from step 1 into a whole-box shipment plan.
    ///
    /// The one hard constraint: <b>a box ships intact</b>. Every unit in a box goes
    /// to one country; only within that country may the units split across stores.
    /// Everything else in this method follows from that.
    ///
    /// Boxes are placed largest first. A box's country is the one whose outstanding
    /// demand the box's contents best satisfy — score = the units of the box that
    /// country still wants, summed over the box's SKUs. Largest-first matters: a big
    /// box placed late has to squeeze into whatever demand is left, while small boxes
    /// can fill gaps afterwards.
    ///
    /// Because a box cannot be split, some units land on stores already at their
    /// target. Those are recorded as OverTarget rather than dropped or hidden — it is
    /// the visible cost of box integrity, and a large total is the signal that box
    /// sizes and store targets disagree.
    /// </summary>
    public async Task<CdcBoxAllocationResult> ProcessBoxAllocationAsync(
        IReadOnlyList<DateTime> lpmDates,
        IReadOnlyList<string> countries,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        if (lpmDates is null || lpmDates.Count == 0)
            return CdcBoxAllocationResult.Fail("Pick at least one LPM.");
        if (countries is null || countries.Count == 0)
            return CdcBoxAllocationResult.Fail("Pick at least one allocation country.");

        var dates = lpmDates.Select(d => d.Date).Distinct().OrderBy(d => d).ToArray();
        var ctry  = countries.Select(x => x.Trim()).Where(x => x.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // ---------- 1. Step 1 must have run, and for THIS scope ----------
        var header = await GetLastRunAsync(ct);
        if (header is null)
            return CdcBoxAllocationResult.Fail(
                "Run \"Allocate DC SOH to Stores\" first — there is no store allocation to ship against.");

        var wantLpm     = string.Join(", ", dates.Select(LpmLabel));
        var wantCountry = string.Join(", ", ctry);
        if (!string.Equals(header.LpmScope, wantLpm, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header.CountryScope, wantCountry, StringComparison.OrdinalIgnoreCase))
        {
            return CdcBoxAllocationResult.Fail(
                $"The store allocation on file is for LPM [{header.LpmScope}] / countries [{header.CountryScope}], " +
                $"but you have selected [{wantLpm}] / [{wantCountry}]. Re-run \"Allocate DC SOH to Stores\" " +
                "for the current selection before processing.");
        }

        // ---------- 2. Targets and boxes ----------
        progress?.Report("Reading store targets…");
        List<CdcDcSohAllocationRow> targets;
        await using (var c = OpenOnPremBackup())
        {
            targets = (await c.QueryAsync<CdcDcSohAllocationRow>(new CommandDefinition(@"
                SELECT Itemcode, DivCode, Country, StoreID, VolumeGroup, DcQty,
                       OtsPercent, AvgOtsPercent, TierName, SkuMaxTier, StoreSoh, AllocatedSoh
                  FROM dbo.DC_STORE_SOH_ALLOCATION WITH (NOLOCK)
                 WHERE AllocatedSoh > 0",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }
        if (targets.Count == 0)
            return CdcBoxAllocationResult.Fail("The store allocation has no positive rows to ship against.");

        progress?.Report("Reading boxes…");
        List<(string BoxNo, DateTime? LpmDt, string ItemCode, int Qty)> boxLines;
        await using (var c = OpenOnPremBackup())
        {
            boxLines = (await c.QueryAsync<(string BoxNo, DateTime? LpmDt, string ItemCode, int Qty)>(
                new CommandDefinition(@"
                    SELECT b.BoxNo,
                           LpmDt = CAST(MAX(b.LPMDt) AS date),
                           b.ItemCode,
                           Qty = SUM(CAST(ISNULL(b.Qty, 0) AS int))
                      FROM racks.dbo.WHBoxItems b WITH (NOLOCK)
                     WHERE b.PalletCategory = 'Eligible'
                       AND ISNULL(b.ShopEligible, '') <> 'E'
                       AND CAST(b.LPMDt AS date) IN @dates
                       AND b.BoxNo IS NOT NULL
                       AND b.ItemCode IS NOT NULL
                     GROUP BY b.BoxNo, b.ItemCode
                    HAVING SUM(CAST(ISNULL(b.Qty, 0) AS int)) > 0",
                    new { dates },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }
        if (boxLines.Count == 0)
            return CdcBoxAllocationResult.Fail($"No eligible boxes for {wantLpm}.");

        // ---------- 3. Demand ledgers ----------
        var divByItem = targets
            .GroupBy(t => t.Itemcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (int?)g.First().DivCode, StringComparer.OrdinalIgnoreCase);

        // Remaining want per (item, country) — drives the box's country choice.
        var countryRemaining = new Dictionary<(string Item, string Country), int>();
        // Remaining want per (item, country, store) — drives the split inside the country.
        var storeRemaining   = new Dictionary<(string Item, string Country, string Store), int>();
        // Every store that carries the item in a country, best-target first. Used for
        // the overflow: a box must land somewhere even when every store is full.
        var storesByItemCountry = new Dictionary<(string Item, string Country), List<string>>();

        foreach (var t in targets)
        {
            var item = t.Itemcode.Trim().ToUpperInvariant();
            var key  = (item, t.Country);
            countryRemaining[key] = countryRemaining.GetValueOrDefault(key, 0) + t.AllocatedSoh;
            storeRemaining[(item, t.Country, t.StoreID)] =
                storeRemaining.GetValueOrDefault((item, t.Country, t.StoreID), 0) + t.AllocatedSoh;
        }
        foreach (var g in targets.GroupBy(t => (Item: t.Itemcode.Trim().ToUpperInvariant(), t.Country)))
            storesByItemCountry[g.Key] = g.OrderByDescending(x => x.AllocatedSoh)
                                          .Select(x => x.StoreID).Distinct().ToList();

        // ---------- 4. Place the boxes ----------
        var boxes = boxLines
            .GroupBy(x => x.BoxNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                BoxNo = g.Key,
                LpmDt = g.Max(x => x.LpmDt),
                Lines = g.Select(x => (Item: x.ItemCode.Trim().ToUpperInvariant(), x.Qty)).ToList(),
                Total = g.Sum(x => x.Qty),
            })
            .OrderByDescending(b => b.Total)
            .ThenBy(b => b.BoxNo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report($"Placing {boxes.Count:N0} boxes across {ctry.Length} countries…");

        var plan     = new List<CdcBoxAllocationRow>();
        var unplaced = new List<CdcUnplacedBoxRow>();
        long qtyPlaced = 0, qtyUnplaced = 0, overTargetTotal = 0;

        foreach (var box in boxes)
        {
            ct.ThrowIfCancellationRequested();

            // Score every country: how much of THIS box does it still want?
            string? best = null;
            var bestScore = -1;
            var bestDepth = -1L;
            foreach (var country in ctry)
            {
                var score = 0;
                long depth = 0;
                foreach (var (item, qty) in box.Lines)
                {
                    var want = countryRemaining.GetValueOrDefault((item, country), 0);
                    score += Math.Min(qty, want);
                    depth += want;
                }
                // Tie-break on total outstanding demand, so an equal score goes to the
                // country with more room left rather than to whichever sorted first.
                if (score > bestScore || (score == bestScore && depth > bestDepth))
                {
                    best = country; bestScore = score; bestDepth = depth;
                }
            }

            if (best is null || bestScore <= 0)
            {
                unplaced.Add(new CdcUnplacedBoxRow(box.BoxNo, box.LpmDt, box.Total, box.Lines.Count,
                    "No selected country still wants any SKU in this box."));
                qtyUnplaced += box.Total;
                continue;
            }

            foreach (var (item, qty) in box.Lines)
            {
                var remaining = qty;

                // Fill the country's stores that still want this SKU, deepest want first.
                var stores = storesByItemCountry.GetValueOrDefault((item, best), new List<string>());
                foreach (var store in stores)
                {
                    if (remaining <= 0) break;
                    var want = storeRemaining.GetValueOrDefault((item, best, store), 0);
                    if (want <= 0) continue;
                    var take = Math.Min(want, remaining);
                    remaining -= take;
                    storeRemaining[(item, best, store)] = want - take;
                    countryRemaining[(item, best)] =
                        Math.Max(0, countryRemaining.GetValueOrDefault((item, best), 0) - take);
                    plan.Add(new CdcBoxAllocationRow(box.BoxNo, box.LpmDt, item,
                        divByItem.GetValueOrDefault(item), best, store, take, take, 0));
                }

                if (remaining <= 0) continue;

                // Box integrity: the rest still has to go, even though every store that
                // wanted this SKU is now full. Put it on the country's best-target store
                // for the SKU, or — when the country has no target row for it at all —
                // on the store with the most outstanding demand overall, so the overflow
                // lands where there is the most capacity to absorb it.
                var fallback = stores.FirstOrDefault()
                    ?? storeRemaining.Keys.Where(k => k.Country == best)
                        .OrderByDescending(k => storeRemaining[k]).Select(k => k.Store).FirstOrDefault();

                if (fallback is null)
                {
                    unplaced.Add(new CdcUnplacedBoxRow(box.BoxNo, box.LpmDt, remaining, 1,
                        $"{item}: {best} has no store carrying this SKU's division."));
                    qtyUnplaced += remaining;
                    continue;
                }

                plan.Add(new CdcBoxAllocationRow(box.BoxNo, box.LpmDt, item,
                    divByItem.GetValueOrDefault(item), best, fallback, remaining, 0, remaining));
                overTargetTotal += remaining;
            }

            qtyPlaced += box.Total;
        }

        // ---------- 5. Persist ----------
        var runTs = NowGst();
        progress?.Report($"Writing {plan.Count:N0} plan rows…");
        await PersistBoxPlanAsync(plan, unplaced, runTs, user.Name, wantLpm, wantCountry, ct);

        if (unplaced.Count > 0)
            warnings.Add($"{unplaced.Count:N0} box line(s) could not be placed — {qtyUnplaced:N0} pcs. See the Unplaced tab.");
        if (overTargetTotal > 0)
            warnings.Add($"{overTargetTotal:N0} pcs went above the store's DC target because boxes ship whole. " +
                         "A large figure here means box sizes and store targets disagree.");

        var boxesPlaced = plan.Select(p => p.BoxNo).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new CdcBoxAllocationResult(
            Success: true, Message: null, RunTS: runTs,
            LpmScope: wantLpm, CountryScope: wantCountry,
            BoxesPlaced: boxesPlaced,
            BoxesUnplaced: unplaced.Select(u => u.BoxNo).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            QtyPlaced: qtyPlaced, QtyUnplaced: qtyUnplaced,
            OverTargetQty: overTargetTotal, Warnings: warnings);
    }

    private async Task PersistBoxPlanAsync(
        List<CdcBoxAllocationRow> plan, List<CdcUnplacedBoxRow> unplaced,
        DateTime runTs, string? runBy, string lpmScope, string countryScope, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("RunTS",        typeof(DateTime));
        dt.Columns.Add("RunBy",        typeof(string));
        dt.Columns.Add("LpmScope",     typeof(string));
        dt.Columns.Add("CountryScope", typeof(string));
        dt.Columns.Add("BoxNo",        typeof(string));
        dt.Columns.Add("LPMDt",        typeof(DateTime));
        dt.Columns.Add("Itemcode",     typeof(string));
        dt.Columns.Add("DivCode",      typeof(int));
        dt.Columns.Add("Country",      typeof(string));
        dt.Columns.Add("StoreID",      typeof(string));
        dt.Columns.Add("Qty",          typeof(int));
        dt.Columns.Add("WithinTarget", typeof(int));
        dt.Columns.Add("OverTarget",   typeof(int));
        foreach (var r in plan)
            dt.Rows.Add(runTs, (object?)runBy ?? DBNull.Value, lpmScope, countryScope,
                        r.BoxNo, (object?)r.LPMDt ?? DBNull.Value, r.Itemcode,
                        (object?)r.DivCode ?? DBNull.Value, r.Country, r.StoreID,
                        r.Qty, r.WithinTarget, r.OverTarget);

        var ut = new DataTable();
        ut.Columns.Add("RunTS",  typeof(DateTime));
        ut.Columns.Add("BoxNo",  typeof(string));
        ut.Columns.Add("LPMDt",  typeof(DateTime));
        ut.Columns.Add("Qty",    typeof(int));
        ut.Columns.Add("Items",  typeof(int));
        ut.Columns.Add("Reason", typeof(string));
        foreach (var u in unplaced)
            ut.Rows.Add(runTs, u.BoxNo, (object?)u.LPMDt ?? DBNull.Value, u.Qty, u.Items,
                        (object?)u.Reason ?? DBNull.Value);

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);

        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.DC_BOX_ALLOCATION; DELETE FROM dbo.DC_BOX_ALLOCATION_UNPLACED;",
            transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        await BulkAsync(c, tx, "dbo.DC_BOX_ALLOCATION", dt, ct);
        await BulkAsync(c, tx, "dbo.DC_BOX_ALLOCATION_UNPLACED", ut, ct);

        await tx.CommitAsync(ct);
    }

    private static async Task BulkAsync(
        SqlConnection c, SqlTransaction tx, string table, DataTable dt, CancellationToken ct)
    {
        if (dt.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = table,
            BatchSize = 5000,
            BulkCopyTimeout = CommandTimeoutSeconds,
        };
        foreach (DataColumn col in dt.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // ===================== Step 2 read-back =====================

    public async Task<CdcBoxAllocationHeader?> GetLastBoxRunAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var row = await c.QueryFirstOrDefaultAsync<(DateTime? RunTS, string? RunBy, string? LpmScope, string? CountryScope, int Boxes, long Qty)>(
            new CommandDefinition(@"
                SELECT TOP 1 RunTS, RunBy, LpmScope, CountryScope,
                       Boxes = COUNT(DISTINCT BoxNo) OVER (),
                       Qty   = SUM(CAST(Qty AS bigint)) OVER ()
                  FROM dbo.DC_BOX_ALLOCATION WITH (NOLOCK)",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return row.RunTS is null
            ? null
            : new CdcBoxAllocationHeader(row.RunTS.Value, row.RunBy, row.LpmScope, row.CountryScope, row.Boxes, row.Qty);
    }

    public async Task<List<CdcBoxCountrySummaryRow>> LoadBoxCountrySummaryAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcBoxCountrySummaryRow>(new CommandDefinition(@"
            SELECT Country,
                   Boxes      = COUNT(DISTINCT BoxNo),
                   Stores     = COUNT(DISTINCT StoreID),
                   Skus       = COUNT(DISTINCT Itemcode),
                   Qty        = SUM(CAST(Qty AS bigint)),
                   OverTarget = SUM(CAST(OverTarget AS bigint))
              FROM dbo.DC_BOX_ALLOCATION WITH (NOLOCK)
             GROUP BY Country
             ORDER BY Qty DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<CdcBoxAllocationRow>> LoadBoxPlanAsync(
        string? boxFilter = null, string? storeFilter = null, int top = 5000, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcBoxAllocationRow>(new CommandDefinition(@"
            SELECT TOP (@top) BoxNo, LPMDt, Itemcode, DivCode, Country, StoreID,
                   Qty, WithinTarget, OverTarget
              FROM dbo.DC_BOX_ALLOCATION WITH (NOLOCK)
             WHERE (@box   IS NULL OR BoxNo   LIKE '%' + @box   + '%')
               AND (@store IS NULL OR StoreID LIKE '%' + @store + '%')
             ORDER BY BoxNo, Itemcode, StoreID",
            new
            {
                top,
                box   = string.IsNullOrWhiteSpace(boxFilter)   ? null : boxFilter.Trim(),
                store = string.IsNullOrWhiteSpace(storeFilter) ? null : storeFilter.Trim(),
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<CdcUnplacedBoxRow>> LoadUnplacedBoxesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcUnplacedBoxRow>(new CommandDefinition(@"
            SELECT BoxNo, LPMDt, Qty, Items, Reason
              FROM dbo.DC_BOX_ALLOCATION_UNPLACED WITH (NOLOCK)
             ORDER BY Qty DESC, BoxNo",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Every box in the plan that somehow reached more than one country. Should always
    /// return nothing — it is the assertion that box integrity actually held, cheap
    /// enough to run after every Process and far better found here than on a loading bay.
    /// </summary>
    public async Task<List<(string BoxNo, int Countries)>> CheckBoxIntegrityAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(string BoxNo, int Countries)>(new CommandDefinition(@"
            SELECT BoxNo, Countries = COUNT(DISTINCT Country)
              FROM dbo.DC_BOX_ALLOCATION WITH (NOLOCK)
             GROUP BY BoxNo
            HAVING COUNT(DISTINCT Country) > 1",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Reference loads =====================

    private async Task<Dictionary<string, int>> LoadDivByItemAsync(string itemCodesCsv, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(string itemcode, int? DivID)>(new CommandDefinition(@"
            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #cdcDivItems FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE CLUSTERED INDEX IX_cdcDivItems ON #cdcDivItems(ItemCode);

            SELECT v.itemcode, MAX(v.DivID) AS DivID
              FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
              INNER JOIN #cdcDivItems i ON i.ItemCode = v.itemcode
             GROUP BY v.itemcode;",
            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) d[r.itemcode.Trim().ToUpperInvariant()] = r.DivID ?? 0;
        return d;
    }

    /// <summary>
    /// OTS from the CDC run table for today (GST). TgtEOM &gt; 50 mirrors PO
    /// allocation — a division a store barely carries is not a real candidate.
    /// </summary>
    private async Task<List<CdcOtsStoreRow>> LoadOtsRunRowsAsync(string[] countries, DateTime nowGst, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        return (await c.QueryAsync<CdcOtsStoreRow>(new CommandDefinition(@"
            SELECT Country, StoreID, DivCode, VolumeGroup,
                   TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh, CountingWIP,
                   OtsQtyToday, OtsPercentToday, ISNULL(CurrentEOW, 0) AS CurrentEOW
              FROM dbo.WmsOtsCdcAllocationRun WITH (NOLOCK)
             WHERE [Month] = @m AND [Year] = @y
               AND OTSDate = @otsDate
               AND TgtEOM > 50
               AND Country IN @countries",
            new { m = nowGst.Month, y = nowGst.Year, otsDate = nowGst.Date, countries },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
    }

    private async Task<Dictionary<(string StoreID, int DivCode), string>> LoadStoreDivGradeAsync(DateTime nowGst, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(string StoreID, int DivCode, string Grade)>(new CommandDefinition(@"
            SELECT StoreID, DivCode, Grade
              FROM dbo.StoreDivGrade WITH (NOLOCK)
             WHERE Month1 = @m AND Year1 = @y
               AND Grade IS NOT NULL AND Grade <> ''",
            new { m = nowGst.Month, y = nowGst.Year },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var d = new Dictionary<(string, int), string>();
        foreach (var r in rows) d[(r.StoreID, r.DivCode)] = r.Grade;
        return d;
    }

    private async Task<Dictionary<(string StoreId, string ItemCode), int>> LoadItemSohByStoreAsync(string itemCodesCsv, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(string storeid, string itemcode, int SOH)>(new CommandDefinition(@"
            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #cdcSohItems FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE CLUSTERED INDEX IX_cdcSohItems ON #cdcSohItems(ItemCode);

            SELECT l.storeid, l.itemcode, SUM(CAST(ISNULL(l.SOH,0) AS INT)) AS SOH
              FROM racks.dbo.LPM_locstock l WITH (NOLOCK)
              INNER JOIN #cdcSohItems i ON i.ItemCode = l.itemcode
             GROUP BY l.storeid, l.itemcode;",
            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var d = new Dictionary<(string, string), int>();
        foreach (var r in rows) d[(r.storeid.ToUpperInvariant(), r.itemcode.ToUpperInvariant())] = r.SOH;
        return d;
    }

    private async Task<Dictionary<string, int>> LoadVolumeGroupOrderAsync(CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(string VolumeGroup, int SortOrder)>(new CommandDefinition(
            "SELECT VolumeGroup, SortOrder FROM dbo.LPM_VolumeGroupRange WITH (NOLOCK)",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) d[r.VolumeGroup] = r.SortOrder;
        return d;
    }

    /// <summary>
    /// Country = 'BFLGROUP' only, the same split PO allocation uses: that row set
    /// carries the full A–Z Volume Group range per division, while the per-country
    /// rows belong to LPMSIM's store allocation.
    /// </summary>
    private async Task<Dictionary<(int DivCode, string VG), List<(int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>>>
        LoadSkuMaxBandsAsync(int[] divs, CancellationToken ct)
    {
        var d = new Dictionary<(int, string), List<(int, int, int?, int?, int?, int?)>>();
        if (divs.Length == 0) return d;

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(int DivCode, string VolumeGroup, int PoQtyFrom, int PoQtyTo, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>(
            new CommandDefinition(@"
                SELECT DivCode, VolumeGroup, PoQtyFrom, PoQtyTo, MinMin, MinMax, IdealMax, MaxMax
                  FROM dbo.LPM_SkuMaxBands WITH (NOLOCK)
                 WHERE DivCode IN @divs
                   AND IsActive = 1
                   AND UPPER(LTRIM(RTRIM(Country))) = 'BFLGROUP'
                 ORDER BY DivCode, VolumeGroup, PoQtyFrom",
                new { divs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        foreach (var r in rows)
        {
            var key = (r.DivCode, r.VolumeGroup ?? "");
            if (!d.TryGetValue(key, out var list)) { list = new(); d[key] = list; }
            list.Add((r.PoQtyFrom, r.PoQtyTo, r.MinMin, r.MinMax, r.IdealMax, r.MaxMax));
        }
        return d;
    }

    private async Task<double> LoadOtsBandPctAsync(CancellationToken ct)
    {
        await using var c = OpenWms();
        var cfg = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 ConfigValue FROM dbo.WmsAppConfig WITH (NOLOCK) WHERE ConfigKey = 'OTSBandPct'",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return double.TryParse(cfg, out var v) && v > 0 ? v : DefaultOtsBandPct;
    }
}
