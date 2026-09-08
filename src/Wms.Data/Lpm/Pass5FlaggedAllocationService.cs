using System.Data;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>A store the planner can pick for Pass 5, with what it would be capped at.</summary>
public sealed record Pass5StoreOption(
    string  StoreID,
    string? StoreName,
    string  Country,
    int     DivCode,
    string? VolumeGroup);

/// <summary>One line of the Pass 5 result, per (Item, Store).</summary>
public sealed record Pass5AllocationRow(
    string ItemCode,
    int    DivCode,
    string Country,
    string StoreID,
    int    Tier,        // the chosen tier's value for this (Div, VG, PoQty)
    int    Soh,
    int    AlreadyAllocated,
    int    Stage1Qty,   // filled up to the tier
    int    Stage2Qty);  // round-robin beyond the tier, once every store was full

/// <summary>Outcome of one Pass 5 run.</summary>
public sealed record Pass5Result(
    bool         Success,
    string?      Message,
    int          FlaggedItems,
    int          FlaggedQty,
    int          AllocatedQty,
    int          Stage1Qty,
    int          Stage2Qty,
    int          UnplacedQty,
    List<string> Warnings)
{
    public static Pass5Result Fail(string message) =>
        new(false, message, 0, 0, 0, 0, 0, 0, new List<string>());
}

/// <summary>
/// Pass 5 — the planner's manual placement of Pass-4 flagged quantity.
///
/// Passes 1-4 flag an item when they cannot place its residual (≥10% of PO qty).
/// That quantity then sits in WmsPlanningFlag doing nothing. Pass 5 lets a planner
/// choose countries, stores and a SKU Max tier, and place it deliberately.
///
/// Two stages, in this order — the second only runs if the first leaves a balance:
///
///   Stage 1  Stores BELOW the chosen tier are filled up to it, round-robin.
///            Headroom = tier − store SOH − what this container already gave them,
///            so a store already at the tier takes nothing here.
///
///   Stage 2  If quantity still remains, the stores already at or above the tier
///            come back in and the balance goes round-robin across ALL selected
///            stores with no cap. This is the planner overriding the tier on
///            purpose, which is the whole point of the button, so it is recorded
///            separately (Stage2Qty) rather than blended into the total.
///
/// Runs only BEFORE approval and before any Azure sync: appending to a container
/// that is already downstream cannot be undone by re-running it.
///
/// Every block that applies to the automatic passes applies here too — a planner
/// choosing a store does not override a division, department, export-country or
/// SkuMax=0 block.
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

    /// <summary>The tiers a planner can pick, in the order the bands define them.</summary>
    public static readonly string[] Tiers = { "MinMin", "MinMax", "IdealMax", "MaxMax" };

    // ===================== Store picker =====================

    /// <summary>
    /// Stores available for Pass 5 on this container: those carrying a flagged
    /// item's division, in the given countries, with a Volume Group for this month.
    /// Blocked stores are left out here rather than filtered later, so the planner
    /// is never offered a store the run would then refuse.
    /// </summary>
    public async Task<List<Pass5StoreOption>> GetStoreOptionsAsync(
        string contno, IReadOnlyCollection<string> countries, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno) || countries.Count == 0) return new();
        var nowGst = NowGst();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<Pass5StoreOption>(new CommandDefinition(@"
            SELECT DISTINCT
                   o.StoreID,
                   -- PBFullname on DataSettings is where every other page reads the
                   -- store's display name from; there is no LPM_StoreMaster here.
                   StoreName   = s.PBFullname,
                   o.Country,
                   o.DivCode,
                   VolumeGroup = g.Grade
              FROM dbo.WmsPlanningFlag f WITH (NOLOCK)
              JOIN dbo.WmsOtsPoAllocationRun o WITH (NOLOCK)
                ON o.DivCode = f.DivCode
               AND o.[Month] = @m AND o.[Year] = @y AND o.OTSDate = @d
              JOIN dbo.StoreDivGrade g WITH (NOLOCK)
                ON g.StoreID = o.StoreID AND g.DivCode = o.DivCode
               AND g.Month1 = @m AND g.Year1 = @y
               AND g.Grade IS NOT NULL AND g.Grade <> ''
              OUTER APPLY (SELECT PBFullname = MAX(ds.PBFullname)
                             FROM bfldata.dbo.DataSettings ds WITH (NOLOCK)
                            WHERE ds.StoreID = o.StoreID) s
             WHERE f.ContNo = @c
               AND f.RemainingQty > 0
               AND o.Country IN @countries
               -- Blocked stores are not offered at all.
               AND NOT EXISTS (SELECT 1 FROM dbo.LPM_StoreDivAccess a WITH (NOLOCK)
                                WHERE a.IsActive = 0
                                  AND UPPER(LTRIM(RTRIM(a.StoreID))) = UPPER(LTRIM(RTRIM(o.StoreID)))
                                  AND a.DivCode = o.DivCode)
             ORDER BY o.Country, o.StoreID",
            new { c = contno.Trim(), m = nowGst.Month, y = nowGst.Year, d = nowGst.Date, countries },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows.AsList();
    }

    // ===================== The run =====================

    public async Task<Pass5Result> AllocateFlaggedAsync(
        string genCountry, string contno, RunOption runOption, string tierName,
        IReadOnlyCollection<string> storeIds, bool commit, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        contno = (contno ?? "").Trim();
        if (string.IsNullOrWhiteSpace(contno)) return Pass5Result.Fail("No container.");
        if (storeIds.Count == 0)              return Pass5Result.Fail("Pick at least one store.");
        if (!Tiers.Contains(tierName))        return Pass5Result.Fail($"Unknown tier '{tierName}'.");

        var roTag = runOption.ToString();

        // ---------- 1. Gate: batch must exist, be unapproved and unsynced ----------
        int batchNo;
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
                return Pass5Result.Fail($"No {roTag} batch for {contno} — process the container first.");
            if (hdr.ApprovedDt is not null)
                return Pass5Result.Fail(
                    $"{contno} was approved on {hdr.ApprovedDt:dd-MMM-yyyy HH:mm}. Pass 5 only runs before " +
                    "approval — appending to an approved batch cannot be undone by re-running it.");
            batchNo = hdr.BatchNo;
        }

        await using (var w = OpenWms())
        {
            var synced = await w.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (synced > 0)
                return Pass5Result.Fail(
                    $"{contno} already has {synced:N0} row(s) synced to Azure WMS. Pass 5 rows would not " +
                    "reach the mirror, so the two would disagree. Clear the Azure rows first.");
        }

        // ---------- 2. Flags, stores, bands, SOH, existing allocation ----------
        await using var conn = OpenOnPremBackup();
        var nowGst = NowGst();

        var flags = (await conn.QueryAsync<(string ItemCode, int DivCode, int PoQty, int RemainingQty)>(
            new CommandDefinition(@"
                SELECT ItemCode, DivCode = ISNULL(DivCode, 0), PoQty, RemainingQty
                  FROM dbo.WmsPlanningFlag WITH (NOLOCK)
                 WHERE ContNo = @c AND RemainingQty > 0 AND ISNULL(DivCode, 0) <> 0",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (flags.Count == 0) return Pass5Result.Fail($"No flagged quantity for {contno}.");

        var picked = storeIds.Select(s => s.Trim()).Where(s => s.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var itemCodesCsv = string.Join(",", flags.Select(f => f.ItemCode.Trim()).Distinct());

        var storeRows = (await conn.QueryAsync<(string StoreID, string Country, int DivCode, string Grade)>(
            new CommandDefinition(@"
                SELECT DISTINCT o.StoreID, o.Country, o.DivCode, g.Grade
                  FROM dbo.WmsOtsPoAllocationRun o WITH (NOLOCK)
                  JOIN dbo.StoreDivGrade g WITH (NOLOCK)
                    ON g.StoreID = o.StoreID AND g.DivCode = o.DivCode
                   AND g.Month1 = @m AND g.Year1 = @y AND g.Grade IS NOT NULL AND g.Grade <> ''
                 WHERE o.[Month] = @m AND o.[Year] = @y AND o.OTSDate = @d
                   AND o.StoreID IN @stores",
            new { m = nowGst.Month, y = nowGst.Year, d = nowGst.Date, stores = picked },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        // (StoreID, DivCode) -> (Country, VG). A store carries a different grade per
        // division, so the key has to include it.
        var storeByKey = storeRows
            .GroupBy(r => (r.StoreID, r.DivCode))
            .ToDictionary(g => g.Key, g => (g.First().Country, VG: g.First().Grade));

        var bands = await LoadBandsAsync(conn, flags.Select(f => f.DivCode).Distinct().ToArray(), ct);
        var soh   = await LoadSohAsync(conn, itemCodesCsv, ct);
        var simBlocked = await LoadSimSkuMaxBlockedAsync(conn, itemCodesCsv, ct);
        var divBlocks  = await LoadDivBlocksAsync(conn, ct);

        // What this container has already given each (store, item) — the tier is an
        // absolute ceiling, so prior allocation counts against it.
        var already = (await conn.QueryAsync<(string StoreID, string Itemcode, int Qty)>(new CommandDefinition(@"
            SELECT StoreID, Itemcode, Qty = SUM(CAST(ISNULL(AllocatedQty, 0) AS int))
              FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
             WHERE BatchNo = @b
             GROUP BY StoreID, Itemcode",
            new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => (r.StoreID.Trim().ToUpperInvariant(), r.Itemcode.Trim().ToUpperInvariant()), r => r.Qty);

        // ---------- 3. Place ----------
        var plan = new List<Pass5AllocationRow>();
        int totalFlagged = 0, totalStage1 = 0, totalStage2 = 0, totalUnplaced = 0;
        var noBand = 0;

        foreach (var f in flags)
        {
            totalFlagged += f.RemainingQty;
            var item = f.ItemCode.Trim().ToUpperInvariant();

            // Candidate stores: picked, carrying this division, not blocked.
            var cands = new List<(string StoreID, string Country, int Tier, int Soh, int Already)>();
            foreach (var sid in picked)
            {
                if (!storeByKey.TryGetValue((sid, f.DivCode), out var st)) continue;
                if (divBlocks.Contains((sid.ToUpperInvariant(), f.DivCode))) continue;
                if (simBlocked.Contains((sid.ToUpperInvariant(), item))) continue;

                var tier = TierFor(bands, f.DivCode, st.VG, f.PoQty, tierName);
                if (tier is null) continue;

                cands.Add((sid, st.Country,
                    tier.Value,
                    soh.GetValueOrDefault((sid.ToUpperInvariant(), item), 0),
                    already.GetValueOrDefault((sid.ToUpperInvariant(), item), 0)));
            }

            if (cands.Count == 0) { noBand++; totalUnplaced += f.RemainingQty; continue; }

            var take = new Dictionary<string, (int S1, int S2)>(StringComparer.OrdinalIgnoreCase);
            var remaining = f.RemainingQty;

            // ---- Stage 1: round-robin up to headroom ----
            var headroom = cands.ToDictionary(
                x => x.StoreID,
                x => Math.Max(0, x.Tier - x.Soh - x.Already),
                StringComparer.OrdinalIgnoreCase);

            while (remaining > 0 && headroom.Values.Any(h => h > 0))
            {
                var gaveThisRound = false;
                foreach (var cnd in cands)
                {
                    if (remaining <= 0) break;
                    if (headroom[cnd.StoreID] <= 0) continue;
                    headroom[cnd.StoreID]--;
                    remaining--;
                    var cur = take.GetValueOrDefault(cnd.StoreID);
                    take[cnd.StoreID] = (cur.S1 + 1, cur.S2);
                    gaveThisRound = true;
                }
                if (!gaveThisRound) break;   // guards against a no-progress loop
            }

            // ---- Stage 2: everyone back in, no cap ----
            while (remaining > 0)
            {
                foreach (var cnd in cands)
                {
                    if (remaining <= 0) break;
                    remaining--;
                    var cur = take.GetValueOrDefault(cnd.StoreID);
                    take[cnd.StoreID] = (cur.S1, cur.S2 + 1);
                }
            }

            foreach (var cnd in cands)
            {
                if (!take.TryGetValue(cnd.StoreID, out var t) || t.S1 + t.S2 == 0) continue;
                totalStage1 += t.S1;
                totalStage2 += t.S2;
                plan.Add(new Pass5AllocationRow(
                    f.ItemCode.Trim(), f.DivCode, cnd.Country, cnd.StoreID,
                    cnd.Tier, cnd.Soh, cnd.Already, t.S1, t.S2));
            }
        }

        if (noBand > 0)
            warnings.Add($"{noBand:N0} flagged item(s) had no selected store with a {tierName} band for their " +
                         "division and grade — their quantity is still unplaced.");
        if (totalStage2 > 0)
            warnings.Add($"{totalStage2:N0} pc(s) went ABOVE the {tierName} tier — every selected store was " +
                         "already full, so the balance was spread round-robin regardless.");

        var allocated = totalStage1 + totalStage2;
        if (!commit)
            return new Pass5Result(true, "Preview only — nothing written.",
                flags.Count, totalFlagged, allocated, totalStage1, totalStage2, totalUnplaced, warnings);

        if (plan.Count == 0)
            return new Pass5Result(true, "Nothing could be placed.",
                flags.Count, totalFlagged, 0, 0, 0, totalUnplaced, warnings);

        // ---------- 4. Write ----------
        await PersistAsync(conn, batchNo, contno, plan, ct);

        return new Pass5Result(true, null,
            flags.Count, totalFlagged, allocated, totalStage1, totalStage2, totalUnplaced, warnings);
    }

    /// <summary>
    /// Top up existing rows, insert rows for stores that had none, and reduce the
    /// flags by what was placed — all in one transaction, because a partial apply
    /// would leave the flag and the allocation disagreeing about what is outstanding.
    ///
    /// A new row is cloned from an existing row of the same item in this batch, which
    /// carries the item-level enrichment (Brand, Division, Season, Style, Size, UPC,
    /// Barcode, GroupCode, POQty …). Only the store-level fields are overridden.
    /// </summary>
    private async Task PersistAsync(
        SqlConnection conn, int batchNo, string contno, List<Pass5AllocationRow> plan, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var r in plan)
        {
            var qty = r.Stage1Qty + r.Stage2Qty;

            var updated = await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WMS_ContAllocationData
                   SET AllocatedQty = ISNULL(AllocatedQty, 0) + @q,
                       Pass5Qty     = ISNULL(Pass5Qty, 0) + @q
                 WHERE BatchNo = @b AND StoreID = @s AND Itemcode = @i",
                new { b = batchNo, s = r.StoreID, i = r.ItemCode, q = qty },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (updated > 0) continue;

            // No row for this (store, item) yet — clone one from the same item.
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO dbo.WMS_ContAllocationData
                    (BatchNo, ContNo, Country, TrnDate, Time1, UPC, Itemcode, Barcode, GroupCode,
                     POQty, SkuMax, AllocatedQty, PrevAllocatedQty, QtyIssue, StoreID, TcmContno,
                     Itemname, BuildingCategory, LPMDt, ORAPONo, Division, Brand, DivCode,
                     Department, Season, Style, Size, SalesPrice, ResultType, FinalResult,
                     Remarks, Pass5Qty, Soh, RawSkuMax)
                SELECT TOP 1
                     t.BatchNo, t.ContNo, @country, t.TrnDate, t.Time1, t.UPC, t.Itemcode, t.Barcode, t.GroupCode,
                     t.POQty, @tier, @q, 0, 0, @store, t.TcmContno,
                     t.Itemname, t.BuildingCategory, t.LPMDt, t.ORAPONo, t.Division, t.Brand, t.DivCode,
                     t.Department, t.Season, t.Style, t.Size, t.SalesPrice, t.ResultType, t.FinalResult,
                     'Pass 5 (planner)', @q, @soh, @tier
                  FROM dbo.WMS_ContAllocationData t WITH (NOLOCK)
                 WHERE t.BatchNo = @b AND t.Itemcode = @i",
                new { b = batchNo, i = r.ItemCode, store = r.StoreID, country = r.Country,
                      q = qty, tier = r.Tier, soh = r.Soh },
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        // Reduce the flags by what was placed; clear the ones fully absorbed.
        foreach (var g in plan.GroupBy(p => p.ItemCode, StringComparer.OrdinalIgnoreCase))
        {
            var placed = g.Sum(x => x.Stage1Qty + x.Stage2Qty);
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

        await tx.CommitAsync(ct);
    }

    // ===================== Lookups =====================

    private static int? TierFor(
        Dictionary<(int, string), List<(int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>> bands,
        int divCode, string? vg, int poQty, string tierName)
    {
        if (!bands.TryGetValue((divCode, vg ?? ""), out var list)) return null;
        foreach (var b in list)
        {
            if (poQty < b.From || poQty > b.To) continue;
            var v = tierName switch
            {
                "MinMin"   => b.MinMin,
                "MinMax"   => b.MinMax,
                "IdealMax" => b.IdealMax,
                _          => b.MaxMax,
            };
            return v is > 0 ? v : null;
        }
        return null;
    }

    private static async Task<Dictionary<(int, string), List<(int, int, int?, int?, int?, int?)>>> LoadBandsAsync(
        SqlConnection c, int[] divs, CancellationToken ct)
    {
        var d = new Dictionary<(int, string), List<(int, int, int?, int?, int?, int?)>>();
        if (divs.Length == 0) return d;
        var rows = await c.QueryAsync<(int DivCode, string VolumeGroup, int PoQtyFrom, int PoQtyTo, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>(
            new CommandDefinition(@"
                SELECT DivCode, VolumeGroup, PoQtyFrom, PoQtyTo, MinMin, MinMax, IdealMax, MaxMax
                  FROM dbo.LPM_SkuMaxBands WITH (NOLOCK)
                 WHERE DivCode IN @divs AND IsActive = 1
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

    private static async Task<Dictionary<(string, string), int>> LoadSohAsync(
        SqlConnection c, string itemCodesCsv, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string storeid, string itemcode, int SOH)>(new CommandDefinition(@"
            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #p5Items FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE CLUSTERED INDEX IX_p5Items ON #p5Items(ItemCode);

            SELECT l.storeid, l.itemcode, SUM(CAST(ISNULL(l.SOH,0) AS INT)) AS SOH
              FROM racks.dbo.LPM_locstock l WITH (NOLOCK)
              INNER JOIN #p5Items i ON i.ItemCode = l.itemcode
             GROUP BY l.storeid, l.itemcode;",
            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var d = new Dictionary<(string, string), int>();
        foreach (var r in rows) d[(r.storeid.ToUpperInvariant(), r.itemcode.ToUpperInvariant())] = r.SOH;
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

    private static async Task<HashSet<(string, int)>> LoadDivBlocksAsync(SqlConnection c, CancellationToken ct)
    {
        var rows = await c.QueryAsync<(string StoreID, int DivCode)>(new CommandDefinition(
            "SELECT StoreID, DivCode FROM dbo.LPM_StoreDivAccess WITH (NOLOCK) WHERE IsActive = 0",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Select(r => ((r.StoreID ?? "").Trim().ToUpperInvariant(), r.DivCode)).ToHashSet();
    }
}
