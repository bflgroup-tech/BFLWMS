using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// One preview line — a (flagged item, candidate store) pair with everything the
/// planner needs to judge it, and the quantity the run proposes.
///
/// NewAllocatedQty is settable: Preview fills it, the planner may overwrite it in
/// the grid, and Submit writes whatever the grid holds. That is the point of the
/// screen — the algorithm proposes, the planner decides.
/// </summary>
public sealed class Pass5PreviewRow
{
    public string   StoreID          { get; set; } = "";
    public string?  StoreName        { get; set; }
    public string   Country          { get; set; } = "";
    public string?  PONo             { get; set; }
    public string   ItemCode         { get; set; } = "";
    public int      DivCode          { get; set; }
    public string?  Brand            { get; set; }
    public int      PoQty            { get; set; }
    public int      AllocatedQty     { get; set; }   // this store already has, for this item
    public int      RemainingQty     { get; set; }   // the item's outstanding flagged qty
    public string?  VolumeGroup      { get; set; }
    public int      OtsFinal         { get; set; }   // OtsQtyToday for (store, div)
    public decimal  AvgOts           { get; set; }   // mean OtsPercentToday across the candidates
    public decimal  OtsPercent       { get; set; }   // this store's own OtsPercentToday (traced, not shown)
    public int      SortRank         { get; set; }   // round-robin position, so the order is reconstructible
    public string?  Lpm              { get; set; }
    public decimal  Turns            { get; set; }   // LPM_DivStoresTurns
    public decimal  AvgTurns         { get; set; }   // mean across the SELECTED stores of that division
    public int?     LatestMerchNeed  { get; set; }   // LPM_OTS_Output.mnwtoday, latest row
    public int      NewAllocatedQty  { get; set; }
}

/// <summary>Outcome of a Preview or a Submit.</summary>
public sealed record Pass5Result(
    bool         Success,
    string?      Message,
    int          FlaggedItems,
    int          FlaggedQty,
    int          AllocatedQty,
    int          UnplacedQty,
    List<string> Warnings)
{
    public static Pass5Result Fail(string message) =>
        new(false, message, 0, 0, 0, 0, new List<string>());
}

/// <summary>
/// Flagged Allocation (Pass 5) — the planner places what Passes 1-4 could not.
///
/// Store selection, in order:
///   1. Every store of the flagged item's division in the selected COUNTRIES whose
///      Volume Group is one of the selected VGs.
///   2. Average Turns (LPM_DivStoresTurns) across THAT set — the selected stores
///      only, so the bar is relative to what the planner picked rather than to
///      stores they excluded.
///   3. Keep the stores strictly above that average.
///   4. Round-robin one unit at a time, highest OTS first.
///
/// No SKU Max tier is involved: the turn filter is what decides who is worth
/// stocking, and the planner edits the result directly.
///
/// Preview computes the whole placement and writes nothing. Submit writes exactly
/// what the grid holds, edits included.
///
/// Runs only BEFORE approval and before any Azure sync — appending to a container
/// that is already downstream cannot be undone by re-running it. Division,
/// department, export-country and SkuMax = 0 blocks all still apply: choosing a
/// store does not override a block.
/// </summary>
public class Pass5FlaggedAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
        {
            ConnectTimeout = ConnectTimeoutSeconds
        };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetWmsAzureConnectionString())
        {
            ConnectTimeout = ConnectTimeoutSeconds
        };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private static DateTime NowGst() => DateTime.UtcNow.AddHours(4);

    // ===================== Filter options =====================

    /// <summary>
    /// Volume Groups present among the stores that could take this container's
    /// flagged items, for the VG filter. Derived from the data rather than a fixed
    /// A..H list, so a VG with no store never appears as a pickable dead end.
    /// </summary>
    public async Task<List<string>> GetVolumeGroupsAsync(
        string contno, IReadOnlyCollection<string> countries, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno) || countries.Count == 0) return new();
        var nowGst = NowGst();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT o.VolumeGroup
              FROM dbo.WmsPlanningFlag f WITH (NOLOCK)
              JOIN dbo.WmsOtsPoAllocationRun o WITH (NOLOCK)
                ON o.DivCode = f.DivCode
               AND o.[Month] = @m AND o.[Year] = @y AND o.OTSDate = @d
               AND o.TgtEOM > 50
             WHERE f.ContNo = @c AND f.RemainingQty > 0
               AND o.Country IN @countries
               AND o.VolumeGroup IS NOT NULL AND LTRIM(RTRIM(o.VolumeGroup)) <> ''
             ORDER BY o.VolumeGroup",
            new { c = contno.Trim(), m = nowGst.Month, y = nowGst.Year, d = nowGst.Date, countries },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Preview =====================

    public async Task<(Pass5Result Result, List<Pass5PreviewRow> Rows)> BuildPreviewAsync(
        string genCountry, string contno, RunOption runOption,
        IReadOnlyCollection<string> countries, IReadOnlyCollection<string> volumeGroups,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();
        contno = (contno ?? "").Trim();
        if (string.IsNullOrWhiteSpace(contno)) return (Pass5Result.Fail("No container."), new());
        if (countries.Count == 0)     return (Pass5Result.Fail("Pick at least one country."), new());
        if (volumeGroups.Count == 0)  return (Pass5Result.Fail("Pick at least one Volume Group."), new());

        var gate = await CheckGateAsync(genCountry, contno, runOption, ct);
        if (gate.Error is not null) return (Pass5Result.Fail(gate.Error), new());
        var batchNo = gate.BatchNo;

        await using var conn = OpenOnPremBackup();
        var nowGst = NowGst();

        var flags = (await conn.QueryAsync<(string ItemCode, int DivCode, string? PONo, int PoQty, int RemainingQty)>(
            new CommandDefinition(@"
                SELECT ItemCode, DivCode = ISNULL(DivCode, 0), PONo, PoQty, RemainingQty
                  FROM dbo.WmsPlanningFlag WITH (NOLOCK)
                 WHERE ContNo = @c AND RemainingQty > 0 AND ISNULL(DivCode, 0) <> 0",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        if (flags.Count == 0) return (Pass5Result.Fail($"No flagged quantity for {contno}."), new());

        var itemCodesCsv = string.Join(",", flags.Select(f => f.ItemCode.Trim()).Distinct());

        // Candidate stores: the selected countries × the selected VGs, carrying a
        // flagged division. Blocked stores are excluded in SQL so they never reach
        // the average — a blocked store must not move the bar it is not competing for.
        var cands = (await conn.QueryAsync<(string StoreID, string? StoreName, string Country, int DivCode,
                                            string VolumeGroup, int OtsQtyToday, decimal OtsPercentToday)>(
            new CommandDefinition(@"
                SELECT o.StoreID, StoreName = s.PBFullname, o.Country, o.DivCode,
                       o.VolumeGroup, o.OtsQtyToday, o.OtsPercentToday
                  FROM dbo.WmsOtsPoAllocationRun o WITH (NOLOCK)
                  OUTER APPLY (SELECT PBFullname = MAX(ds.PBFullname)
                                 FROM bfldata.dbo.DataSettings ds WITH (NOLOCK)
                                WHERE ds.StoreID = o.StoreID) s
                 WHERE o.[Month] = @m AND o.[Year] = @y AND o.OTSDate = @d
                   AND o.TgtEOM > 50
                   AND o.Country IN @countries
                   AND o.VolumeGroup IN @vgs
                   AND NOT EXISTS (SELECT 1 FROM dbo.LPM_StoreDivAccess a WITH (NOLOCK)
                                    WHERE a.IsActive = 0
                                      AND UPPER(LTRIM(RTRIM(a.StoreID))) = UPPER(LTRIM(RTRIM(o.StoreID)))
                                      AND a.DivCode = o.DivCode)",
                new { m = nowGst.Month, y = nowGst.Year, d = nowGst.Date, countries, vgs = volumeGroups },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (cands.Count == 0)
            return (Pass5Result.Fail("No store matches those countries and Volume Groups."), new());

        // Export-country division blocks. Kept out of the candidate query on purpose:
        // LPM_Ex2LocationConfig may be absent, and the engine degrades to "no export
        // blocks" rather than failing the run — a joined-in table would fail it.
        var ex2 = await LoadEx2CountryDivBlocksAsync(conn, ct);
        if (ex2.Count > 0)
        {
            var before = cands.Count;
            cands = cands
                .Where(s => !ex2.Contains((s.Country.Trim().ToUpperInvariant(), s.DivCode)))
                .ToList();
            if (cands.Count == 0)
                return (Pass5Result.Fail(
                    "Every candidate store is in a country whose division is blocked at the Ex2 export " +
                    "location, so none of them can be allocated to."), new());
            if (cands.Count < before)
                warnings.Add($"{before - cands.Count:N0} store/division pair(s) excluded by an Ex2 " +
                             "export-country block.");
        }

        var turns   = await LoadTurnsAsync(conn, ct);
        var mnw     = await LoadMerchNeedAsync(conn, ct);
        var simBlk  = await LoadSimSkuMaxBlockedAsync(conn, itemCodesCsv, ct);
        var brandBy = await LoadBrandAsync(conn, contno, ct);
        var lpmBy   = await LoadLpmAsync(conn, contno, ct);

        var alreadyStoreItem = (await conn.QueryAsync<(string StoreID, string Itemcode, int Qty)>(
            new CommandDefinition(@"
                SELECT StoreID, Itemcode, Qty = SUM(CAST(ISNULL(AllocatedQty,0) AS int))
                  FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                 WHERE BatchNo = @b GROUP BY StoreID, Itemcode",
                new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => (r.StoreID.Trim().ToUpperInvariant(), r.Itemcode.Trim().ToUpperInvariant()), r => r.Qty);

        var allocByItem = alreadyStoreItem
            .GroupBy(kv => kv.Key.Item2)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

        var poByItem = (await conn.QueryAsync<(string ItemCode, int Qty)>(new CommandDefinition(@"
            SELECT ItemCode, Qty = SUM(CAST(ISNULL(orgqty,0) AS int))
              FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
             WHERE ContNo = @c GROUP BY ItemCode",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => r.ItemCode.Trim().ToUpperInvariant(), r => r.Qty);

        // ---------- place ----------
        var rows = new List<Pass5PreviewRow>();
        int totalFlagged = 0, totalPlaced = 0, totalUnplaced = 0, staleQty = 0, noStoreItems = 0;

        var byDiv = cands.GroupBy(c => c.DivCode).ToDictionary(g => g.Key, g => g.ToList());

        // Country is upper-cased on BOTH sides, like StoreID. LPM_DivStoresTurns is
        // loaded by hand and its Country will not reliably agree in case with
        // WmsOtsPoAllocationRun ('Uae' vs 'UAE') — a case-sensitive key would miss
        // every row, read every turn as 0, and quietly place nothing. That is the
        // same trap that once starved four countries in the ADM band lookup.
        decimal TurnOf((string StoreID, string? StoreName, string Country, int DivCode,
                        string VolumeGroup, int OtsQtyToday, decimal OtsPercentToday) s) =>
            turns.GetValueOrDefault(
                (s.Country.Trim().ToUpperInvariant(), s.StoreID.Trim().ToUpperInvariant(), s.DivCode), 0m);

        // Distinguishes "turns are flat" from "there are no turns at all", so the
        // warning can name the real cause instead of leaving the planner guessing.
        var candsWithTurns = cands.Count(s => TurnOf(s) > 0m);

        foreach (var f in flags)
        {
            totalFlagged += f.RemainingQty;
            var item = f.ItemCode.Trim().ToUpperInvariant();

            // Never place more than the item is genuinely short by — a flag left
            // behind by a run that died claims quantity that was never dropped.
            var outstanding = Math.Max(0,
                poByItem.GetValueOrDefault(item, 0) - allocByItem.GetValueOrDefault(item, 0));
            var placeable = Math.Min(f.RemainingQty, outstanding);
            staleQty += f.RemainingQty - placeable;
            if (placeable <= 0) continue;

            if (!byDiv.TryGetValue(f.DivCode, out var divStores) || divStores.Count == 0)
            {
                noStoreItems++; totalUnplaced += placeable; continue;
            }

            var usable = divStores.Where(s => !simBlk.Contains((s.StoreID.ToUpperInvariant(), item))).ToList();
            if (usable.Count == 0) { noStoreItems++; totalUnplaced += placeable; continue; }

            // Avg Turns across the SELECTED stores of this division, then keep the
            // ones strictly above it. A store with no turns row counts as 0 — absent
            // data is not evidence of a good turn.
            var avgTurns = usable.Average(TurnOf);
            var avgOts   = Math.Round(usable.Average(s => s.OtsPercentToday), 2);

            var above = usable
                .Where(s => TurnOf(s) > avgTurns)
                .OrderByDescending(s => s.OtsQtyToday)
                .ToList();

            // When nothing clears the average — flat turns, or no turns loaded at all —
            // the candidates are still listed with a proposed 0 rather than dropped off
            // the screen. An empty grid gives the planner nothing to override; a listed
            // store with 0 in New Allocated Qty can simply be typed into.
            var placing = above.Count > 0;
            var ranked  = placing ? above : usable.OrderByDescending(s => s.OtsQtyToday).ToList();

            var take = new int[ranked.Count];
            if (placing)
            {
                // Round-robin, highest OTS first.
                var remaining = placeable;
                while (remaining > 0)
                {
                    for (var i = 0; i < ranked.Count && remaining > 0; i++) { take[i]++; remaining--; }
                }
            }
            else
            {
                noStoreItems++; totalUnplaced += placeable;
            }

            for (var i = 0; i < ranked.Count; i++)
            {
                var s = ranked[i];
                rows.Add(new Pass5PreviewRow
                {
                    StoreID = s.StoreID, StoreName = s.StoreName, Country = s.Country,
                    PONo = f.PONo, ItemCode = f.ItemCode.Trim(), DivCode = f.DivCode,
                    Brand = brandBy.GetValueOrDefault(item),
                    PoQty = f.PoQty,
                    AllocatedQty = alreadyStoreItem.GetValueOrDefault((s.StoreID.ToUpperInvariant(), item), 0),
                    RemainingQty = placeable,
                    VolumeGroup = s.VolumeGroup,
                    OtsFinal = s.OtsQtyToday,
                    AvgOts = avgOts,
                    OtsPercent = s.OtsPercentToday,
                    SortRank = i,
                    Lpm = lpmBy.GetValueOrDefault(item),
                    Turns = TurnOf(s),
                    AvgTurns = Math.Round(avgTurns, 4),
                    LatestMerchNeed = mnw.GetValueOrDefault((s.StoreID, f.DivCode)),
                    NewAllocatedQty = take[i],
                });
                totalPlaced += take[i];
            }
        }

        if (staleQty > 0)
            warnings.Add($"{staleQty:N0} flagged pc(s) are NOT outstanding - left by a run that died before " +
                         "saving. Excluded.");
        if (noStoreItems > 0)
        {
            if (candsWithTurns == 0)
                warnings.Add(
                    $"No turns found in LPM_DivStoresTurns for ANY of the {cands.Count:N0} candidate " +
                    "store/division rows, so every turn reads 0 and nothing can be above average. " +
                    "Load that table (Country / StoreID / DivCode / Turns) - or type the quantities " +
                    "in by hand below and Submit.");
            else
                warnings.Add(
                    $"{noStoreItems:N0} item(s) had no store above average turns in the selected " +
                    "countries/VGs - listed below with 0 so you can place them by hand.");
        }

        return (new Pass5Result(true, null, flags.Count, totalFlagged, totalPlaced, totalUnplaced, warnings), rows);
    }

    // ===================== Submit =====================

    /// <summary>
    /// Write exactly what the grid holds. The planner may have edited
    /// NewAllocatedQty, so this does not recompute — it takes the rows as given and
    /// only refuses quantities that would push an item past its PO qty.
    /// </summary>
    public async Task<Pass5Result> SubmitAsync(
        string genCountry, string contno, RunOption runOption,
        IReadOnlyCollection<Pass5PreviewRow> rows, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        contno = (contno ?? "").Trim();
        var live = rows.Where(r => r.NewAllocatedQty > 0).ToList();
        if (live.Count == 0) return Pass5Result.Fail("Nothing to submit — every New Allocated Qty is zero.");

        var gate = await CheckGateAsync(genCountry, contno, runOption, ct);
        if (gate.Error is not null) return Pass5Result.Fail(gate.Error);
        var batchNo = gate.BatchNo;

        await using var conn = OpenOnPremBackup();

        // Re-check the ceiling against live data, not against what Preview saw. The
        // grid is editable and may have been sitting open while something else moved.
        var poByItem = (await conn.QueryAsync<(string ItemCode, int Qty)>(new CommandDefinition(@"
            SELECT ItemCode, Qty = SUM(CAST(ISNULL(orgqty,0) AS int))
              FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
             WHERE ContNo = @c GROUP BY ItemCode",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => r.ItemCode.Trim().ToUpperInvariant(), r => r.Qty);

        var allocByItem = (await conn.QueryAsync<(string Itemcode, int Qty)>(new CommandDefinition(@"
            SELECT Itemcode, Qty = SUM(CAST(ISNULL(AllocatedQty,0) AS int))
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
             WHERE BatchNo = @b GROUP BY Itemcode",
            new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => r.Itemcode.Trim().ToUpperInvariant(), r => r.Qty);

        var trimmed = 0;
        foreach (var g in live.GroupBy(r => r.ItemCode.Trim().ToUpperInvariant()))
        {
            var headroom = Math.Max(0, poByItem.GetValueOrDefault(g.Key, 0) - allocByItem.GetValueOrDefault(g.Key, 0));
            var asked = g.Sum(r => r.NewAllocatedQty);
            if (asked <= headroom) continue;

            // Trim from the smallest rows up, so the planner's biggest deliberate
            // placements survive and the loss lands where it matters least.
            var over = asked - headroom;
            trimmed += over;
            foreach (var r in g.OrderBy(r => r.NewAllocatedQty))
            {
                if (over <= 0) break;
                var cut = Math.Min(over, r.NewAllocatedQty);
                r.NewAllocatedQty -= cut;
                over -= cut;
            }
        }
        if (trimmed > 0)
            warnings.Add($"{trimmed:N0} pc(s) trimmed - the edited quantities exceeded what those items are " +
                         "still short by, which would have allocated past the PO.");

        live = live.Where(r => r.NewAllocatedQty > 0).ToList();
        if (live.Count == 0)
            return new Pass5Result(true, "Nothing written — every quantity was trimmed to zero.", 0, 0, 0, 0, warnings);

        await PersistAsync(conn, batchNo, contno, live, runOption, ct);

        var placed = live.Sum(r => r.NewAllocatedQty);
        return new Pass5Result(true, null,
            live.Select(r => r.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            placed, placed, 0, warnings);
    }

    private async Task PersistAsync(
        SqlConnection conn, int batchNo, string contno, List<Pass5PreviewRow> rows,
        RunOption runOption, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var r in rows)
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WMS_ContAllocationData
                   SET AllocatedQty = ISNULL(AllocatedQty, 0) + @q,
                       Pass5Qty     = ISNULL(Pass5Qty, 0) + @q
                 WHERE BatchNo = @b AND StoreID = @s AND Itemcode = @i",
                new { b = batchNo, s = r.StoreID, i = r.ItemCode, q = r.NewAllocatedQty },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (updated > 0) continue;

            // No row for this (store, item) — clone one of the same item for the
            // item-level enrichment and override only the store-level fields.
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO dbo.WMS_ContAllocationData
                    (BatchNo, ContNo, Country, TrnDate, Time1, UPC, Itemcode, Barcode, GroupCode,
                     POQty, SkuMax, AllocatedQty, PrevAllocatedQty, QtyIssue, StoreID, TcmContno,
                     Itemname, BuildingCategory, LPMDt, ORAPONo, Division, Brand, DivCode,
                     Department, Season, Style, Size, SalesPrice, ResultType, FinalResult,
                     Remarks, Pass5Qty)
                SELECT TOP 1
                     t.BatchNo, t.ContNo, @country, t.TrnDate, t.Time1, t.UPC, t.Itemcode, t.Barcode, t.GroupCode,
                     t.POQty, 0, @q, 0, 0, @store, t.TcmContno,
                     t.Itemname, t.BuildingCategory, t.LPMDt, t.ORAPONo, t.Division, t.Brand, t.DivCode,
                     t.Department, t.Season, t.Style, t.Size, t.SalesPrice, t.ResultType, t.FinalResult,
                     'Pass 5 (planner)', @q
                  FROM dbo.WMS_ContAllocationData t WITH (NOLOCK)
                 WHERE t.BatchNo = @b AND t.Itemcode = @i",
                new { b = batchNo, i = r.ItemCode, store = r.StoreID, country = r.Country, q = r.NewAllocatedQty },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        foreach (var g in rows.GroupBy(r => r.ItemCode, StringComparer.OrdinalIgnoreCase))
        {
            var placed = g.Sum(x => x.NewAllocatedQty);
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WmsPlanningFlag
                   SET RemainingQty = CASE WHEN RemainingQty - @p < 0 THEN 0 ELSE RemainingQty - @p END
                 WHERE ContNo = @c AND ItemCode = @i",
                new { c = contno, i = g.Key, p = placed },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.WmsPlanningFlag WHERE ContNo = @c AND RemainingQty <= 0",
            new { c = contno }, transaction: tx,
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // Trace: Pass 4's Flagged rows described a shortfall that no longer exists.
        // They go first — left beside the Pass 5 rows they would double-count the item.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.WmsAllocationTrace WHERE ContNo = @c AND StoreID = 'Flagged'",
            new { c = contno }, transaction: tx,
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        foreach (var r in rows)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO dbo.WmsAllocationTrace
                    (ContNo, Itemcode, StoreID, DivCode, Pass, SortRank, VolumeGroup, TierName,
                     LiveOtsPctBefore, Cap, Soh, CurrentBeforeTake, RemainingBefore, Take, RemainingAfter,
                     RunningOtsQtyAfter, RunOption, RunBy, SkipReason,
                     AvgOtsPercent, InitialOtsPct, PONo, POLineSizeQty, Country)
                VALUES (@c, @i, @s, @d, 5, @rank, @vg, 'Pass5',
                        @otspct, 0, 0, @cur, @rem, @take, @after,
                        @otsqty, @ro, @by, @skip,
                        @avgots, @otspct, @po, @poqty, @ctry)",
                new
                {
                    c = contno, i = r.ItemCode, s = r.StoreID, d = r.DivCode,
                    rank = r.SortRank, vg = r.VolumeGroup,
                    // Cap is 0 because no tier cap applies in Pass 5 — the turn filter
                    // decided who is eligible, and the planner decided the quantity.
                    // The turn that qualified the store is recorded in SkipReason.
                    otspct = r.OtsPercent, otsqty = r.OtsFinal,
                    cur = r.AllocatedQty, rem = r.RemainingQty, take = r.NewAllocatedQty,
                    after = Math.Max(0, r.RemainingQty - r.NewAllocatedQty),
                    ro = runOption.ToString(), by = user.Name,
                    skip = Clip($"P5 turn {r.Turns:0.##}>{r.AvgTurns:0.##}", 30),
                    avgots = r.AvgOts, po = r.PONo, poqty = r.PoQty, ctry = r.Country,
                },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        // Whatever Pass 5 could not place is still flagged, so it needs its own
        // Flagged trace row again — with the NEW smaller quantity. The engine's
        // convention is Cap = Take = the dropped remainder, so that SUM(Take) per
        // (ContNo, Itemcode) still reconciles to the PO line qty. Deleting the old
        // rows without writing these back would leave the trace short by the
        // still-unplaced quantity.
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO dbo.WmsAllocationTrace
                (ContNo, Itemcode, StoreID, DivCode, Pass, SortRank, VolumeGroup, TierName,
                 LiveOtsPctBefore, Cap, Soh, CurrentBeforeTake, RemainingBefore, Take, RemainingAfter,
                 RunningOtsQtyAfter, RunOption, RunBy, SkipReason, PONo, POLineSizeQty, Country)
            SELECT @c, f.ItemCode, 'Flagged', ISNULL(f.DivCode, 0), 4, 0, NULL, 'Flagged',
                   NULL, f.RemainingQty, 0, 0, f.RemainingQty, f.RemainingQty, 0,
                   0, @ro, @by, 'Flagged: after Pass 5', f.PONo, f.PoQty, NULL
              FROM dbo.WmsPlanningFlag f
             WHERE f.ContNo = @c AND f.RemainingQty > 0",
            new { c = contno, ro = runOption.ToString(), by = user.Name },
            transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    // ===================== Gate + lookups =====================

    private async Task<(int BatchNo, string? Error)> CheckGateAsync(
        string genCountry, string contno, RunOption runOption, CancellationToken ct)
    {
        var roTag = runOption.ToString();
        await using (var c = OpenOnPremBackup())
        {
            var hdr = await c.QueryFirstOrDefaultAsync<(int BatchNo, DateTime? ApprovedDt)>(new CommandDefinition(@"
                SELECT TOP 1 BatchNo, ApprovedDt
                  FROM dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
                 WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro
                 ORDER BY BatchNo DESC",
                new { gc = genCountry, c = contno, ro = roTag },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (hdr.BatchNo == 0)
                return (0, $"No {roTag} batch for {contno} — process the container first.");
            if (hdr.ApprovedDt is not null)
                return (0, $"{contno} was approved on {hdr.ApprovedDt:dd-MMM-yyyy HH:mm}. Pass 5 only runs " +
                           "before approval — appending to an approved batch cannot be undone by re-running it.");

            await using var w = OpenWms();
            var synced = await w.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (synced > 0)
                return (0, $"{contno} already has {synced:N0} row(s) synced to Azure WMS. Pass 5 rows would " +
                           "not reach the mirror, so the two would disagree. Clear the Azure rows first.");

            return (hdr.BatchNo, null);
        }
    }

    /// <summary>
    /// Export-country division blocks, same shape and same casing discipline as the
    /// engine's own loader: LPM_Ex2LocationConfig.Country disagrees in case with every
    /// other source, so both sides are upper-cased here and at lookup.
    /// </summary>
    private static async Task<HashSet<(string Country, int DivCode)>> LoadEx2CountryDivBlocksAsync(
        SqlConnection c, CancellationToken ct)
    {
        try
        {
            return (await c.QueryAsync<(string Country, int DivCode)>(new CommandDefinition(@"
                SELECT UPPER(LTRIM(RTRIM(cfg.Country))) AS Country, a.DivCode
                  FROM dbo.LPM_StoreDivAccess a WITH (NOLOCK)
                  JOIN dbo.LPM_Ex2LocationConfig cfg WITH (NOLOCK)
                    ON UPPER(LTRIM(RTRIM(cfg.Ex2StoreID))) = UPPER(LTRIM(RTRIM(a.StoreID)))
                 WHERE a.IsActive = 0
                   AND UPPER(LTRIM(RTRIM(a.Country))) = 'EX2LOCATIONS'
                   AND cfg.Country IS NOT NULL",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Where(r => !string.IsNullOrWhiteSpace(r.Country))
                .Select(r => (r.Country.Trim(), r.DivCode))
                .ToHashSet();
        }
        catch
        {
            // Table absent -> no export-country blocks, matching the engine.
            return new HashSet<(string, int)>();
        }
    }

    /// <summary>Per-store, per-division turn. Missing rows read as 0 downstream.</summary>
    private static async Task<Dictionary<(string Country, string StoreID, int DivCode), decimal>> LoadTurnsAsync(
        SqlConnection c, CancellationToken ct)
    {
        try
        {
            var rows = await c.QueryAsync<(string Country, string StoreID, int DivCode, decimal Turns)>(
                new CommandDefinition(
                    "SELECT Country, StoreID, DivCode, Turns FROM dbo.LPM_DivStoresTurns WITH (NOLOCK)",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var d = new Dictionary<(string, string, int), decimal>();
            // Country upper-cased here and at every lookup — see TurnOf in BuildPreviewAsync.
            foreach (var r in rows)
                d[(r.Country.Trim().ToUpperInvariant(), r.StoreID.Trim().ToUpperInvariant(), r.DivCode)] = r.Turns;
            return d;
        }
        catch
        {
            // Table not deployed / empty -> every store reads 0, the average is 0, and
            // nothing is strictly above it. The caller reports that as "no store above
            // average" rather than silently allocating to everyone.
            return new();
        }
    }

    private static async Task<Dictionary<(string StoreID, int DivCode), int?>> LoadMerchNeedAsync(
        SqlConnection c, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string StoreID, int DivCode, int? MnwToday)>(new CommandDefinition(@"
            WITH latest AS (
                SELECT StoreID, DivCode, mnwtoday,
                       rn = ROW_NUMBER() OVER (PARTITION BY StoreID, DivCode ORDER BY OTSDate DESC)
                  FROM dbo.LPM_OTS_Output WITH (NOLOCK)
            )
            SELECT StoreID, DivCode, mnwtoday AS MnwToday FROM latest WHERE rn = 1",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var d = new Dictionary<(string, int), int?>();
        foreach (var r in rows) d[(r.StoreID, r.DivCode)] = r.MnwToday;
        return d;
    }

    private static async Task<Dictionary<string, string?>> LoadBrandAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string itemcode, string? Brand)>(new CommandDefinition(@"
            SELECT itemcode, Brand = MAX(vendor)
              FROM usa.dbo.USAOrgFile WITH (NOLOCK)
             WHERE ContNo = @c GROUP BY itemcode",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) d[r.itemcode.Trim().ToUpperInvariant()] = r.Brand;
        return d;
    }

    private static async Task<Dictionary<string, string?>> LoadLpmAsync(
        SqlConnection c, string contno, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string ItemCode, string? LPM)>(new CommandDefinition(@"
            SELECT ItemCode, LPM = MAX(LPM)
              FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
             WHERE ContNo = @c GROUP BY ItemCode",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) d[r.ItemCode.Trim().ToUpperInvariant()] = r.LPM;
        return d;
    }

    private static async Task<HashSet<(string, string)>> LoadSimSkuMaxBlockedAsync(
        SqlConnection c, string itemCodesCsv, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string StoreId, string Itemcode, int SkuMax)>(new CommandDefinition(@"
            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #p5Sku FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE CLUSTERED INDEX IX_p5Sku ON #p5Sku(ItemCode);

            SELECT s.StoreId, s.Itemcode, ISNULL(s.SkuMax, 0) AS SkuMax
              FROM dbo.LPM_SimItemSkuMax s WITH (NOLOCK)
              INNER JOIN #p5Sku i ON i.ItemCode = s.Itemcode;",
            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var set = new HashSet<(string, string)>();
        foreach (var r in rows)
            if (r.SkuMax == 0) set.Add((r.StoreId.ToUpperInvariant(), r.Itemcode.ToUpperInvariant()));
        return set;
    }

    /// <summary>WmsAllocationTrace.SkipReason is NVARCHAR(30) and bulk inserts abort rather than truncate.</summary>
    private static string? Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
