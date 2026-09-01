using System.Data;
using System.Text.RegularExpressions;
using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Container Allocation Process service.
///
/// Phase 1 (this file): UI inputs (Country, WH, Contno), "Load PO Data"
/// grid from usa.usaorgfile_LPM, and a multi-step Process validation
/// (Contreceipt + KNBBoxes + 3-way qty match + not-yet-building + not-yet-completed).
///
/// Phase 2 (TBD): the actual process — writes to LPMSIM.dbo.WMS_ContAllocationData.
///
/// Connections used:
///   - OnPremBackupDB (the UAE backup) — hosts usa, bfldata, hodata, LPMSIM
///     databases via 3-part naming. Used for every validation read + the
///     Phase-2 insert.
///   - Azure WMS DB — for WmsOpenBox (is anyone already building?) and
///     WmsBuildingCompletion (is the container already completed?).
/// </summary>
public class ContainerAllocationService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    // Default 15s post-login timeout is too tight when the on-prem SQL is busy
    // (saw a 14003ms post-login on a real Process call). Bump to 60s for this
    // service only — every other caller stays on the configured default.
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

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

    // ===================== Load PO Data =====================
    public async Task<List<PoDataRow>> LoadPoDataAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        contno = contno.Trim();

        await using var c = OpenOnPremBackup();
        // ContReceiptDT comes from bfldata.dbo.Contreceipt joined on TCMNo.
        // Buyer / LPM / Division / orgqty come from usa.dbo.usaorgfile_LPM.
        // Column names follow the existing on-prem schema — adjust if any
        // are slightly different (e.g. Vendor vs Buyer).
        // Sources:
        //   usaorgfile_LPM   — ContNo, OraPONo, LPM, ItemCode, orgqty
        //   Contreceipt      — ReceiptDt (via TCMNo)
        //   vUSAOrder        — OthersPath = Buyer, country = DestCountry (subqueries)
        //   vupc_subclass    — Division (via itemcode)
        //   USAOrgFile       — vendor = Brand (via ContNo + itemcode)
        var rows = await c.QueryAsync<PoDataRow>(new CommandDefinition(@"
            SELECT
                u.ContNo                              AS Contno,
                MAX(cr.ReceiptDt)                     AS ContReceiptDT,
                u.OraPONo                             AS PONO,
                u.LPM                                 AS LPM,
                (SELECT TOP 1 OthersPath FROM hodata.dbo.vUSAOrder WHERE refno = u.ContNo)  AS Buyer,
                MAX(sub.Division)                     AS Division,
                MAX(org.vendor)                       AS Brand,
                CAST(ISNULL(SUM(u.orgqty), 0) AS INT) AS Qty,
                (SELECT TOP 1 country     FROM hodata.dbo.vUSAOrder WHERE refno = u.ContNo) AS DestCountry
            FROM usa.dbo.usaorgfile_LPM u WITH (NOLOCK)
            LEFT JOIN bfldata.dbo.Contreceipt cr           WITH (NOLOCK) ON cr.TCMNo    = u.ContNo
            LEFT JOIN datareporting.dbo.vupc_subclass sub  WITH (NOLOCK) ON sub.itemcode = u.ItemCode
            LEFT JOIN usa.dbo.USAOrgFile org               WITH (NOLOCK) ON org.ContNo  = u.ContNo AND org.itemcode = u.ItemCode
            WHERE u.ContNo = @contno
            GROUP BY u.ContNo, u.OraPONo, u.LPM
            ORDER BY u.OraPONo, u.LPM",
            new { contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Validate (Process button — Phase 1) =====================
    // 5 logical steps, but only 4 round-trips:
    //   1) Contreceipt.TCMNo                              — 1 round-trip (gate)
    //   2) usa.KNBBoxes                                   — 1 round-trip (gate)
    //   3) 3-way qty sums                                 — 1 round-trip (3 sub-selects)
    //   4+5) WmsOpenBox + WmsBuildingCompletion           — 1 round-trip (combined)
    public async Task<ContainerAllocationValidationResult> ValidateAsync(
        string country, string contno,
        IProgress<AllocationProgress>? progress = null,
        RunOption runOption = RunOption.FillSKUMax,
        IReadOnlyCollection<string>? allocationCountries = null,
        // Only the Manual mode consumes WmsManualAllocation, so only it needs the
        // sheet present. Defaulted true so an omitted argument keeps the old
        // (stricter) behaviour rather than silently dropping the gate.
        bool ecomManualPriority = true,
        CancellationToken ct = default)
    {
        var steps = new List<ValidationStep>();
        if (string.IsNullOrWhiteSpace(contno))
        {
            steps.Add(new ValidationStep("Inputs", false, "Container number is required."));
            return new ContainerAllocationValidationResult(false, steps);
        }
        if (string.IsNullOrWhiteSpace(country))
        {
            steps.Add(new ValidationStep("Inputs", false, "Country is required."));
            return new ContainerAllocationValidationResult(false, steps);
        }
        contno = contno.Trim();
        const int TOTAL = 7;

        await using (var c = OpenOnPremBackup())
        {
            // 1. Contreceipt.TCMNo
            progress?.Report(new AllocationProgress(1, TOTAL, "Validating: Contreceipt"));
            var ok = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM bfldata.dbo.Contreceipt WITH (NOLOCK) WHERE TCMNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in bfldata.Contreceipt (TCMNo)",
                ok,
                ok ? null : $"No row in bfldata.Contreceipt with TCMNo = '{contno}'."));
            if (!ok) return new ContainerAllocationValidationResult(false, steps);

            // 2. usa.KNBBoxes
            progress?.Report(new AllocationProgress(2, TOTAL, "Validating: KNBBoxes"));
            var ok2 = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM usa.dbo.KNBBoxes WITH (NOLOCK) WHERE contno = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
            steps.Add(new ValidationStep(
                "Container exists in usa.KNBBoxes",
                ok2,
                ok2 ? null : $"No row in usa.KNBBoxes with contno = '{contno}'."));
            if (!ok2) return new ContainerAllocationValidationResult(false, steps);

            // 3. Three-way qty match — combined into ONE round-trip (3 sub-selects).
            progress?.Report(new AllocationProgress(3, TOTAL, "Validating: three-way qty match"));
            var qty = await c.QueryFirstAsync<(int Q1, int Q2, int Q3)>(new CommandDefinition(@"
                SELECT
                    (SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.USAOrgFile     WITH (NOLOCK) WHERE ContNo = @c) AS Q1,
                    (SELECT ISNULL(SUM(orgqty),0) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo = @c) AS Q2,
                    (SELECT ISNULL(SUM(qty),0)    FROM hodata.dbo.vUSAOrder   WITH (NOLOCK) WHERE refno  = @c) AS Q3",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var qtyOk = qty.Q1 > 0 && qty.Q1 == qty.Q2 && qty.Q2 == qty.Q3;
            steps.Add(new ValidationStep(
                "Three-way qty match (USAOrgFile = usaorgfile_LPM = hodata.vUSAOrder)",
                qtyOk,
                qtyOk ? $"All three = {qty.Q1}." : $"USAOrgFile={qty.Q1}, usaorgfile_LPM={qty.Q2}, vUSAOrder={qty.Q3} — must be > 0 and equal."));
            if (!qtyOk) return new ContainerAllocationValidationResult(false, steps);
        }

        // 4+5+6+7: Azure WMS build/sync status — one round-trip, 4 sub-counts.
        progress?.Report(new AllocationProgress(4, TOTAL, "Validating: build status"));
        await using (var w = OpenWms())
        {
            var status = await w.QueryFirstAsync<(int Building, int Completed, int AllocSynced, int Scanned)>(new CommandDefinition(@"
                SELECT
                    (SELECT COUNT(*) FROM dbo.WmsOpenBox            WITH (NOLOCK) WHERE Contno = @c)                                 AS Building,
                    (SELECT COUNT(*) FROM dbo.WmsBuildingCompletion WITH (NOLOCK) WHERE Country = @ct AND ContNo = @c)                AS Completed,
                    (SELECT COUNT(*) FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c)                                 AS AllocSynced,
                    (SELECT COUNT(*) FROM dbo.WMSContBuildScanData  WITH (NOLOCK) WHERE ContNo = @c)                                 AS Scanned",
                new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var building = status.Building > 0;
            steps.Add(new ValidationStep(
                "Container not yet being built (no WmsOpenBox row)",
                !building,
                building ? $"Container {contno} already has open box(es) in WmsOpenBox." : null));
            if (building) return new ContainerAllocationValidationResult(false, steps);

            progress?.Report(new AllocationProgress(5, TOTAL, "Validating: completion status"));
            var completed = status.Completed > 0;
            steps.Add(new ValidationStep(
                "Container not yet completed (no WmsBuildingCompletion row)",
                !completed,
                completed ? $"Container {contno} already completed for country {country}." : null));
            if (completed) return new ContainerAllocationValidationResult(false, steps);

            progress?.Report(new AllocationProgress(6, TOTAL, "Validating: allocation not yet synced to Azure"));
            var allocSynced = status.AllocSynced > 0;
            steps.Add(new ValidationStep(
                "Container not yet synced to Azure allocation (no WMS_ContAllocationData row)",
                !allocSynced,
                allocSynced
                    ? $"Container {contno} already has {status.AllocSynced} row(s) in dbo.WMS_ContAllocationData — its allocation was already approved and synced. Delete those first if you really want to re-allocate."
                    : null));
            if (allocSynced) return new ContainerAllocationValidationResult(false, steps);

            progress?.Report(new AllocationProgress(7, TOTAL, "Validating: no prior building scans"));
            var scanned = status.Scanned > 0;
            steps.Add(new ValidationStep(
                "No prior building scans (no WMSContBuildScanData row)",
                !scanned,
                scanned
                    ? $"Container {contno} already has {status.Scanned} scan row(s) in dbo.WMSContBuildScanData — building has started. Cannot re-run allocation."
                    : null));
            if (scanned) return new ContainerAllocationValidationResult(false, steps);

            // 8. Manual Allocation ECOM gate — only for FillSKUMax+RoundRobin
            //    when the operator included ECOM in the Allocation Countries.
            //    ECOM has a single store (StoreID='ONLINE') and its per-store
            //    caps come from dbo.WmsManualAllocation. If that table has no
            //    ONLINE row for this container, FillSKUMax+RR has nothing to
            //    cap ECOM against — block Process here so the operator uploads
            //    the sheet first.
            //    Manual mode ONLY. Under "Follow PO Allocation logic" ONLINE competes
            //    as an ordinary store on Volume Group + SKU Max tier and the manual
            //    sheet is never read, so demanding it would block a run that has no
            //    use for it.
            var wantsEcom = ecomManualPriority
                            && (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers)
                            && allocationCountries is not null
                            && allocationCountries.Any(x => string.Equals(x, "ECOM", StringComparison.OrdinalIgnoreCase));
            if (wantsEcom)
            {
                progress?.Report(new AllocationProgress(7, TOTAL, "Validating: ECOM Manual Allocation rows"));
                var ecomRows = await w.ExecuteScalarAsync<int?>(new CommandDefinition(
                    @"SELECT TOP 1 1 FROM dbo.WmsManualAllocation WITH (NOLOCK)
                       WHERE ContNo = @c AND StoreID = 'ONLINE'",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) == 1;
                steps.Add(new ValidationStep(
                    "ECOM Manual Allocation rows exist (StoreID='ONLINE')",
                    ecomRows,
                    ecomRows
                        ? null
                        : $"Allocation Countries includes ECOM but dbo.WmsManualAllocation has no row for ContNo={contno}, StoreID='ONLINE'. Upload ECOM's Manual Allocation sheet before running FillSKUMax+RoundRobin."));
                if (!ecomRows) return new ContainerAllocationValidationResult(false, steps);
            }

            // 9. Daily-OTS gate — for OTS-run based algorithms, the OTS PO
            //    Allocation report must have been Generated today (GST).
            //    Enforces the daily refresh chain: VG -> OTS -> Container
            //    Allocation. Skipping any link means the container inherits
            //    a stale snapshot.
            if (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers)
            {
                progress?.Report(new AllocationProgress(8, TOTAL, "Validating: OTS Generated today"));
                var todayGst = DateTime.UtcNow.AddHours(4).Date;
                await using var opb = OpenOnPremBackup();
                var otsToday = await opb.ExecuteScalarAsync<int>(new CommandDefinition(
                    @"SELECT COUNT(1) FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
                       WHERE OTSDate = @dt",
                    new { dt = todayGst }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                var otsToday_ok = otsToday > 0;
                steps.Add(new ValidationStep(
                    "OTS for PO Allocation generated today",
                    otsToday_ok,
                    otsToday_ok
                        ? null
                        : $"OTS for PO Allocation has not been Generated today ({todayGst:dd/MM/yyyy} GST). Go to OTS for PO Allocation → Generate first, then re-run Process."));
                if (!otsToday_ok) return new ContainerAllocationValidationResult(false, steps);
            }
        }

        return new ContainerAllocationValidationResult(true, steps);
    }

    // ===================== Process — preview allocation =====================
    // Walks each PO line item, looks up DivCode from vupc_subclass, finds
    // eligible stores via LPM_EOM_Output × LPM_SKUMaxRule (current month),
    // orders eligible stores by current OTS DESC (nulls last), and assigns
    // min(SKUMax, remaining) per store. If qty remains after all stores
    // hit cap, does round-robin one piece per store in same order until
    // qty hits zero.
    public async Task<AllocationProcessResult> ProcessAllocationAsync(
        string contno,
        IProgress<AllocationProgress>? progress = null,
        RunOption runOption = RunOption.FillSKUMax,
        IReadOnlyCollection<string>? allocationCountries = null,
        bool ecomManualPriority = false,
        bool traceEnabled = false,
        bool bypassPass1b = false,
        CancellationToken ct = default)
    {
        var result  = new List<AllocationRow>();
        var blocked = new List<BlockedItemRow>();
        // Trace is only meaningful for the two OTS-run based algorithms; the
        // simpler FillSKUMax / RoundRobin paths don't have distinct passes to
        // record. Callers still pass the flag, we just no-op here.
        var traceOn = traceEnabled &&
            (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers);
        var trace = traceOn ? new List<AllocationTraceRow>() : null;
        // Bypass Pass 1b audit — accumulated during FMMPO processing when the
        // operator ticks the checkbox. Flushed at the end into dbo.Pass1ByPass
        // (delete-by-ContNo then bulk insert, same lifecycle as WmsAllocationTrace).
        var pass1BypassAudit = (bypassPass1b && runOption == RunOption.FillMinMinPlusOthers)
            ? new List<(string PONo, string Itemcode, int PoQty, int ABCMax, int ABCSOH, int ABCReqdStock, decimal MinMinCoverPct)>()
            : null;
        if (string.IsNullOrWhiteSpace(contno)) return new(result, blocked, trace);
        contno = contno.Trim();

        // Everything in this method is prefetch-heavy — the per-line loop below
        // depends on ~15 on-prem lookups plus 3-4 Azure ones. We fan them out
        // into two parallel waves so the operator sees Process finish in
        // ~= slowest-single-query time instead of sum-of-all-queries time.
        //
        //   Wave 1 (fires as soon as PO lines are known):
        //     itemMeta, deptBlocks, divBlocks, orgByItem, storeNameById,
        //     palletByStore, priorityByStoreDiv, mnwByStoreDiv,
        //     sales prices (one task per country in parallel),
        //     WmsBuildingCompletion (Azure), receiptDt, initialAlloc+topN (Azure, RR),
        //     WmsOtsPoAllocationRun (Azure, RR)
        //
        //   Wave 2 (fires once wave 1's divByItem is known):
        //     eomStores, rulesRaw (SKU Max bands), otsRaw (LPM_OTS_Output),
        //     approvedRows (LPMSIM anti-joined to WmsBuildingCompletion)
        //
        // Each parallel task opens its own SqlConnection because Dapper +
        // SqlConnection aren't thread-safe for concurrent commands.

        progress?.Report(new AllocationProgress(0, 0, "Prefetching: PO line items"));
        List<(string ContNo, string OraPONo, string ItemCode, int Qty, string? LPM, DateTime? LPMDt)> lines;
        await using (var c0 = OpenOnPremBackup())
        {
            lines = (await c0.QueryAsync<(string ContNo, string OraPONo, string ItemCode, int Qty, string? LPM, DateTime? LPMDt)>(
                new CommandDefinition(@"
                    SELECT ContNo, OraPONo, ItemCode,
                           CAST(ISNULL(orgqty,0) AS INT) AS Qty,
                           LPM, LPMDt
                    FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
                    WHERE ContNo = @c
                    ORDER BY OraPONo, LPM, ItemCode",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }
        if (lines.Count == 0) return new(result, blocked, trace);

        var distinctItemCodes = lines.Select(l => l.ItemCode).Distinct().ToArray();

        // Wave 1's item lookups used `itemcode IN @items`, which Dapper expands to ONE
        // PARAMETER PER CODE. On a large container that is a statement carrying a
        // thousand-plus parameters — a fresh plan compiled every run, no reuse, and the
        // salesprice lookup repeats it once PER ALLOCATION COUNTRY (eight of them on a
        // full run), so the cost multiplies. It also sits under SQL Server's hard
        // 2100-parameter ceiling, which a big enough container would eventually hit.
        //
        // Same fix already used by ManualAllocationService and JafzaRoboProductionService:
        // send the list as ONE csv parameter, split it into an indexed temp table
        // server-side, then join. One plan, one parameter, no ceiling.
        //
        // Every task below opens its own connection, so the #temp tables are
        // session-scoped and cannot collide despite running in parallel.
        var itemCodesCsv = string.Join(",", distinctItemCodes);
        var hasCountryFilter  = allocationCountries is { Count: > 0 };
        var countryFilter     = hasCountryFilter ? allocationCountries!.ToArray() : Array.Empty<string>();
        var nowGst            = DateTime.UtcNow.AddHours(4);

        // ================= Wave 1 =================
        progress?.Report(new AllocationProgress(0, 0, "Prefetching: wave 1 (11 lookups in parallel)"));

        async Task<Dictionary<string, (int? DivID, string? Division, string? Department)>> LoadItemMeta()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string itemcode, int? DivID, string? Division, string? Department)>(new CommandDefinition(@"
                SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caMetaItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                CREATE CLUSTERED INDEX IX_caMetaItems ON #caMetaItems(ItemCode);

                SELECT v.itemcode, v.DivID, v.Division, v.Department
                FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                INNER JOIN #caMetaItems i ON i.ItemCode = v.itemcode;",
                new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .GroupBy(r => r.itemcode)
                .ToDictionary(g => g.Key, g => (g.First().DivID, g.First().Division, g.First().Department), StringComparer.OrdinalIgnoreCase);
        }

        async Task<HashSet<(string Sid, int DivCode, string Dep)>> LoadDeptBlocks()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreID, int DivCode, string? Department)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, Department
                FROM dbo.LPM_StoreDeptAccess WITH (NOLOCK)
                WHERE IsActive = 0",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(r => (Sid: (r.StoreID ?? "").Trim().ToUpperInvariant(), r.DivCode, Dep: (r.Department ?? "").Trim().ToUpperInvariant()))
                .ToHashSet();
        }

        async Task<HashSet<(string Sid, int DivCode)>> LoadDivBlocks()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreID, int DivCode)>(new CommandDefinition(@"
                SELECT StoreID, DivCode
                FROM dbo.LPM_StoreDivAccess WITH (NOLOCK)
                WHERE IsActive = 0",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(r => (Sid: (r.StoreID ?? "").Trim().ToUpperInvariant(), r.DivCode))
                .ToHashSet();
        }

        async Task<Dictionary<string, (string? itemname, string? vendor, string? season, string? Style, string? Size)>> LoadOrgByItem()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string itemcode, string? itemname, string? vendor, string? season, string? Style, string? Size)>(new CommandDefinition(@"
                SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caOrgItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                CREATE CLUSTERED INDEX IX_caOrgItems ON #caOrgItems(ItemCode);

                SELECT o.itemcode,
                       MAX(o.itemname) AS itemname,
                       MAX(o.vendor)   AS vendor,
                       MAX(o.season)   AS season,
                       MAX(o.Style)    AS Style,
                       MAX(o.[Size])   AS [Size]
                FROM usa.dbo.USAOrgFile o WITH (NOLOCK)
                INNER JOIN #caOrgItems i ON i.ItemCode = o.itemcode
                WHERE o.contno = @c
                GROUP BY o.itemcode;",
                new { c = contno, itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.itemcode, r => (r.itemname, r.vendor, r.season, r.Style, r.Size), StringComparer.OrdinalIgnoreCase);
        }

        async Task<Dictionary<string, string?>> LoadStoreNames()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreID, string? PBFullname)>(new CommandDefinition(@"
                SELECT StoreID, MAX(PBFullname) AS PBFullname
                FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                WHERE PBFullname IS NOT NULL
                GROUP BY StoreID",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.StoreID, r => r.PBFullname, StringComparer.OrdinalIgnoreCase);
        }

        async Task<Dictionary<string, (string? PalletTypeS, string? PalletTypeW)>> LoadPalletByStore()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreId, string? PalletTypeS, string? PalletTypeW)>(new CommandDefinition(@"
                SELECT StoreId, MAX(PalletTypeS) AS PalletTypeS, MAX(PalletTypeW) AS PalletTypeW
                FROM dbo.WMS_Building_PalletTypes WITH (NOLOCK)
                WHERE StoreId IS NOT NULL
                GROUP BY StoreId",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToDictionary(r => r.StoreId, r => (r.PalletTypeS, r.PalletTypeW), StringComparer.OrdinalIgnoreCase);
        }

        async Task<Dictionary<(string StoreID, int DivCode), int?>> LoadPriority()
        {
            await using var c1 = OpenOnPremBackup();
            var pRows = await c1.QueryAsync<(string StoreID, int DivCode, int? PriorityRank)>(new CommandDefinition(
                @"SELECT StoreId AS StoreID, DivCode, PriorityRank
                    FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                   WHERE Month1 = @m AND Year1 = @y",
                new { m = nowGst.Month, y = nowGst.Year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var d = new Dictionary<(string StoreID, int DivCode), int?>();
            foreach (var p in pRows) d[(p.StoreID, p.DivCode)] = p.PriorityRank;
            return d;
        }

        async Task<Dictionary<(string StoreID, int DivCode), int?>> LoadMnw()
        {
            await using var c1 = OpenOnPremBackup();
            var mRows = await c1.QueryAsync<(string StoreID, int DivCode, int? MnwToday)>(new CommandDefinition(
                @"WITH latest AS (
                     SELECT StoreID, DivCode, mnwtoday,
                            rn = ROW_NUMBER() OVER (PARTITION BY StoreID, DivCode ORDER BY OTSDate DESC)
                       FROM dbo.LPM_OTS_Output WITH (NOLOCK)
                  )
                  SELECT StoreID, DivCode, mnwtoday AS MnwToday
                    FROM latest WHERE rn = 1",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            var d = new Dictionary<(string StoreID, int DivCode), int?>();
            foreach (var m in mRows) d[(m.StoreID, m.DivCode)] = m.MnwToday;
            return d;
        }

        // Sales prices — fan out one Task per country. UAE uses hodata.salesprice,
        // other countries use <DataName>.dbo.RFSalesprice via a per-country DataName
        // lookup. Failures are logged but don't abort the whole prefetch.
        async Task<Dictionary<(string Country, string ItemCode), decimal?>> LoadSalesPrices()
        {
            var result = new Dictionary<(string Country, string ItemCode), decimal?>();
            if (!hasCountryFilter || distinctItemCodes.Length == 0) return result;

            async Task<(string Country, List<(string itemcode, decimal? salesrate)> Rows)?> LoadOneCountry(string sc)
            {
                try
                {
                    await using var cc = OpenOnPremBackup();
                    if (string.Equals(sc, "UAE", StringComparison.OrdinalIgnoreCase))
                    {
                        var rows = (await cc.QueryAsync<(string itemcode, decimal? salesrate)>(new CommandDefinition(@"
                            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caPriceItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                            CREATE CLUSTERED INDEX IX_caPriceItems ON #caPriceItems(ItemCode);

                            SELECT p.itemcode, p.salesrate
                            FROM hodata.dbo.salesprice p WITH (NOLOCK)
                            INNER JOIN #caPriceItems i ON i.ItemCode = p.itemcode;",
                            new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
                        return (sc, rows);
                    }
                    var dataName = await cc.ExecuteScalarAsync<string?>(new CommandDefinition(@"
                        SELECT TOP 1 DataName FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                        WHERE SIMCountry = @c
                          AND DataName IS NOT NULL AND LTRIM(RTRIM(DataName)) <> ''",
                        new { c = sc }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    if (string.IsNullOrWhiteSpace(dataName)) return (sc, new());
                    if (!Regex.IsMatch(dataName, @"^[A-Za-z0-9_]+$")) return (sc, new());
                    var sql = $@"
                        SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caRfPriceItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                        CREATE CLUSTERED INDEX IX_caRfPriceItems ON #caRfPriceItems(ItemCode);

                        SELECT p.itemcode, p.salesrate
                          FROM {dataName}.dbo.RFSalesprice p WITH (NOLOCK)
                          INNER JOIN #caRfPriceItems i ON i.ItemCode = p.itemcode;";
                    var rows2 = (await cc.QueryAsync<(string itemcode, decimal? salesrate)>(new CommandDefinition(
                        sql, new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
                    return (sc, rows2);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ContainerAllocation] WARN: SalesPrice lookup failed for country '{sc}': {ex.Message}");
                    return null;
                }
            }

            var tasks = countryFilter
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(LoadOneCountry)
                .ToArray();
            var perCountry = await Task.WhenAll(tasks);
            foreach (var pc in perCountry)
            {
                if (pc is null) continue;
                foreach (var r in pc.Value.Rows) result[(pc.Value.Country, r.itemcode)] = r.salesrate;
            }
            return result;
        }

        async Task<HashSet<(string ContNo, string Country)>> LoadCompletedSet()
        {
            await using var w = OpenWms();
            var compRows = await w.QueryAsync<(string ContNo, string Country)>(new CommandDefinition(
                "SELECT DISTINCT ContNo, Country FROM dbo.WmsBuildingCompletion WITH (NOLOCK)",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return compRows.Select(r => (r.ContNo, r.Country)).ToHashSet();
        }

        async Task<DateTime?> LoadContainerReceiptDt()
        {
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return null;
            await using var c1 = OpenOnPremBackup();
            return await c1.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                @"SELECT TOP 1 receiptdt FROM bfldata.dbo.contreceipt WITH (NOLOCK)
                   WHERE refno = @c
                   ORDER BY receiptdt DESC",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        async Task<(Dictionary<(string StoreID, string ItemCode), int> InitialAlloc, int TopN)> LoadInitialAllocAndTopN()
        {
            var initialAlloc = new Dictionary<(string StoreID, string ItemCode), int>();
            var topN = 25;
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return (initialAlloc, topN);
            await using var wms = new SqlConnection(resolver.GetWmsAzureConnectionString());
            await wms.OpenAsync(ct);
            var rows = await wms.QueryAsync<(string StoreID, string Itemcode, int AllocationQty)>(new CommandDefinition(
                @"SELECT StoreID, Itemcode, AllocationQty
                    FROM dbo.WmsManualAllocation WITH (NOLOCK)
                   WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows)
                initialAlloc[(r.StoreID.ToUpperInvariant(), r.Itemcode.ToUpperInvariant())] = r.AllocationQty;

            var cfg = await wms.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT TOP 1 ConfigValue FROM dbo.WmsAppConfig WITH (NOLOCK)
                   WHERE ConfigKey = 'ContainerAlloc.FillSKUMaxRoundRobin.TopN'",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (int.TryParse(cfg, out var t) && t > 0) topN = t;
            return (initialAlloc, topN);
        }

        // FillSKUMax+RR only: pre-fetch per-(StoreId, Itemcode) SOH from
        // racks.LPM_locstock. This drives the CapFor calculation
        //   cap = max(0, LPM_SKUMaxRule.SkuMax - SOH(store, item))
        // Using WmsOtsPoAllocationRun.SOHToday (division-level) here gave
        // cap=0 for every store because per-item band SkuMax (~18) got
        // subtracted from a much larger division-level SOH.
        async Task<Dictionary<(string StoreId, string ItemCode), int>> LoadItemSohByStore()
        {
            var d = new Dictionary<(string, string), int>();
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers || distinctItemCodes.Length == 0)
                return d;
            await using var c1 = OpenOnPremBackup();
            var rows = await c1.QueryAsync<(string storeid, string itemcode, int SOH)>(new CommandDefinition(@"
                SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caSohItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                CREATE CLUSTERED INDEX IX_caSohItems ON #caSohItems(ItemCode);

                SELECT l.storeid, l.itemcode, SUM(CAST(ISNULL(l.SOH,0) AS INT)) AS SOH
                  FROM racks.dbo.LPM_locstock l WITH (NOLOCK)
                  INNER JOIN #caSohItems i ON i.ItemCode = l.itemcode
                 GROUP BY l.storeid, l.itemcode;",
                new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows)
                d[(r.storeid.ToUpperInvariant(), r.itemcode.ToUpperInvariant())] = r.SOH;
            return d;
        }

        // FillSKUMax+RR only: pre-fetch per-(StoreId, Itemcode) SKU Max from
        // LPMSIM.dbo.LPM_SimItemSkuMax. Any (Store, Item) with a value of 0
        // is a hard block for that combination — the algorithm won't allocate
        // to it, and it won't fall back to the LPM_SKUMaxRule band. (Item,
        // Store) pairs missing from the table proceed with the band logic
        // as before.
        // Ceiling on how much of a container's PO Qty a country may take, per
        // DIVISION. Keyed by (Country, DivCode) from the allocation rows, so 'ECOM'
        // caps the online channel.
        //
        // DivCode 0 is the country-wide default; a specific DivCode overrides it.
        // Resolution is exact -> default -> uncapped, so a country with no row at
        // all allocates unrestricted. A missing row must never silently zero
        // someone's allocation.
        async Task<Dictionary<(string Country, int DivCode), decimal>> LoadPoAllocationMaxPct()
        {
            var d = new Dictionary<(string, int), decimal>();
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers)
                return d;
            try
            {
                await using var c1 = OpenOnPremBackup();
                var rows = await c1.QueryAsync<(string Country, int DivCode, decimal Pct)>(new CommandDefinition(
                    @"SELECT Country, ISNULL(DivCode, 0) AS DivCode, POAllocationMaxPct
                        FROM LPMSIM.dbo.LPM_POAllocationMaxPct WITH (NOLOCK)
                       WHERE Country IS NOT NULL AND POAllocationMaxPct > 0",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in rows)
                    d[(r.Country.Trim().ToUpperInvariant(), r.DivCode)] = r.Pct;
            }
            catch
            {
                // Table (or the DivCode column) not deployed yet -> no caps, same as
                // before the cap existed.
            }
            return d;
        }

        async Task<HashSet<(string StoreId, string ItemCode)>> LoadSimSkuMaxBlocked()
        {
            var blocked = new HashSet<(string, string)>();
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers || distinctItemCodes.Length == 0)
                return blocked;
            await using var c1 = OpenOnPremBackup();
            var rows = await c1.QueryAsync<(string StoreId, string Itemcode, int SkuMax)>(new CommandDefinition(
                @"SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #caSkuMaxItems FROM STRING_SPLIT(@itemCodesCsv, ',');
                  CREATE CLUSTERED INDEX IX_caSkuMaxItems ON #caSkuMaxItems(ItemCode);

                  SELECT s.StoreId, s.Itemcode, ISNULL(s.SkuMax, 0) AS SkuMax
                    FROM LPMSIM.dbo.LPM_SimItemSkuMax s WITH (NOLOCK)
                    INNER JOIN #caSkuMaxItems i ON i.ItemCode = s.Itemcode;",
                new { itemCodesCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows)
                if (r.SkuMax == 0)
                    blocked.Add((r.StoreId.ToUpperInvariant(), r.Itemcode.ToUpperInvariant()));
            return blocked;
        }

        async Task<List<OtsRunLookupRow>> LoadOtsRunRows()
        {
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return new();
            await using var wms = OpenOnPremBackup();
            // OTSDate = today (GST) is REQUIRED, not incidental. Without it the query
            // returned every OTSDate in the month and the dictionary build below —
            // which has no ORDER BY and is last-write-wins — silently picked an
            // arbitrary day per (StoreID, DivCode). Allocation then ran on stale OTS
            // while the Validate step, which checks COUNT(*) WHERE OTSDate = today,
            // still reported OTS as generated. Observed on AEINT8070: the trace showed
            // LiveOtsPct 92.37 against a CurrentEOW of 13,170 from an earlier day,
            // where that day's actual row was CurrentEOW 5,882 / OtsPercent -45.04.
            return (await wms.QueryAsync<OtsRunLookupRow>(new CommandDefinition(@"
                SELECT Country, StoreID, DivCode, VolumeGroup,
                       TgtEOM, SOHToday, WeekSales, InTransit, Ex2DcSoh, CountingWIP,
                       OtsQtyToday, OtsPercentToday, ISNULL(CurrentEOW, 0) AS CurrentEOW
                  FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
                 WHERE [Month] = @m AND [Year] = @y
                   AND OTSDate = @otsDate
                   AND TgtEOM > 50
                   AND (@noCountryFilter = 1 OR Country IN @countries)",
                new { m = nowGst.Month, y = nowGst.Year, otsDate = nowGst.Date,
                      noCountryFilter = hasCountryFilter ? 0 : 1,
                      countries = countryFilter },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }

        async Task<double> LoadOtsBandPct()
        {
            // AvgOts +/- band width (percentage points) used to pick the SKUMax tier
            // in Fill SKUMAX + RR. Default 10pp; runtime knob on WmsAppConfig.
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return 10.0;
            await using var wms = new SqlConnection(resolver.GetWmsAzureConnectionString());
            await wms.OpenAsync(ct);
            var cfg = await wms.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT TOP 1 ConfigValue FROM dbo.WmsAppConfig WITH (NOLOCK)
                   WHERE ConfigKey = 'OTSBandPct'",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return double.TryParse(cfg, out var v) && v > 0 ? v : 10.0;
        }

        async Task<Dictionary<string, int>> LoadVolumeGroupOrder()
        {
            // VolumeGroup priority order for Fill SKUMAX + RR. SortOrder on
            // LPM_VolumeGroupRange is authoritative; missing groups fall to 999.
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return d;
            await using var c1 = OpenOnPremBackup();
            var rows = await c1.QueryAsync<(string VolumeGroup, int SortOrder)>(new CommandDefinition(
                @"SELECT VolumeGroup, SortOrder
                    FROM LPMSIM.dbo.LPM_VolumeGroupRange WITH (NOLOCK)",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows) d[r.VolumeGroup] = r.SortOrder;
            return d;
        }

        // Fan out wave 1.
        var w1_itemMeta       = LoadItemMeta();
        var w1_deptBlocks     = LoadDeptBlocks();
        var w1_divBlocks      = LoadDivBlocks();
        var w1_orgByItem      = LoadOrgByItem();
        var w1_storeNameById  = LoadStoreNames();
        var w1_palletByStore  = LoadPalletByStore();
        var w1_priority       = LoadPriority();
        var w1_mnw            = LoadMnw();
        var w1_prices         = LoadSalesPrices();
        var w1_completed      = LoadCompletedSet();
        var w1_receiptDt      = LoadContainerReceiptDt();
        var w1_initialAlloc   = LoadInitialAllocAndTopN();
        var w1_otsRunRows     = LoadOtsRunRows();
        var w1_poMaxPct         = LoadPoAllocationMaxPct();
        var w1_simSkuMaxBlocked = LoadSimSkuMaxBlocked();
        var w1_itemSohByStore = LoadItemSohByStore();
        var w1_otsBandPct     = LoadOtsBandPct();
        var w1_vgOrder        = LoadVolumeGroupOrder();

        await Task.WhenAll(
            w1_itemMeta, w1_deptBlocks, w1_divBlocks, w1_orgByItem,
            w1_storeNameById, w1_palletByStore, w1_priority, w1_mnw,
            w1_prices, w1_completed, w1_receiptDt, w1_initialAlloc, w1_otsRunRows,
            w1_simSkuMaxBlocked, w1_itemSohByStore, w1_otsBandPct, w1_vgOrder, w1_poMaxPct);

        var itemMeta          = await w1_itemMeta;
        var deptBlocks        = await w1_deptBlocks;
        var divBlocks         = await w1_divBlocks;
        var orgByItem         = await w1_orgByItem;
        var storeNameById     = await w1_storeNameById;
        var palletByStore     = await w1_palletByStore;
        var priorityByStoreDiv = await w1_priority;
        var mnwByStoreDiv     = await w1_mnw;
        var pricesByCountryItem = await w1_prices;
        var completed         = await w1_completed;
        var containerReceiptDt = await w1_receiptDt;
        var (initialAllocByKey, fillRRTopN) = await w1_initialAlloc;
        var otsRunRowsList    = await w1_otsRunRows;
        var poMaxPctByCountry = await w1_poMaxPct;

        const string EcomCountry = "ECOM";
        static bool IsEcomStore(string? storeId) =>
            string.Equals(storeId?.Trim(), "ONLINE", StringComparison.OrdinalIgnoreCase);
        var simSkuMaxBlocked  = await w1_simSkuMaxBlocked;
        var itemSohByStore    = await w1_itemSohByStore;
        var otsBandPct        = await w1_otsBandPct;
        var vgSortOrder       = await w1_vgOrder;

        var divByItem = itemMeta.ToDictionary(kv => kv.Key, kv => kv.Value.DivID ?? 0, StringComparer.OrdinalIgnoreCase);

        // ECOM's ceiling, PER DIVISION: a percentage of that division's PO Qty
        // within this container, from LPM_POAllocationMaxPct.
        //
        // Per division rather than per container so a division's budget can only be
        // spent by its own items. A single container-wide budget was consumed by
        // whichever divisions happened to be processed first, leaving later ones
        // nothing regardless of their size.
        //
        // FLOOR, not round: rounding up would let ECOM exceed the configured %.
        // A division with no percentage configured is uncapped, matching pre-cap
        // behaviour — an unseeded division must never silently zero a country.
        //
        // Percentage resolution per division: exact (ECOM, div), then the (ECOM, 0)
        // country-wide default, then nothing.
        decimal? EcomPctFor(int divCode) =>
            poMaxPctByCountry.TryGetValue((EcomCountry, divCode), out var exact) ? exact
            : poMaxPctByCountry.TryGetValue((EcomCountry, 0), out var dflt)      ? dflt
            : null;

        var ecomAllowanceByDiv = new Dictionary<int, int>();
        foreach (var g in lines.GroupBy(l => divByItem.TryGetValue(l.ItemCode, out var d) ? d : 0))
        {
            var pct = EcomPctFor(g.Key);
            if (pct is not > 0) continue;           // absent from the dictionary = uncapped
            ecomAllowanceByDiv[g.Key] =
                (int)Math.Min(int.MaxValue,
                    (long)Math.Floor(g.Sum(l => (long)l.Qty) * (double)pct.Value / 100.0));
        }
        var ecomTakenByDiv = new Dictionary<int, int>();

        int EcomRemainingAllowance(int divCode)
        {
            // No entry = this division has no configured percentage = uncapped.
            if (!ecomAllowanceByDiv.TryGetValue(divCode, out var allow)) return int.MaxValue;
            var taken = ecomTakenByDiv.TryGetValue(divCode, out var t) ? t : 0;
            return Math.Max(0, allow - taken);
        }

        void SpendEcom(int divCode, int qty)
        {
            if (qty <= 0 || !ecomAllowanceByDiv.ContainsKey(divCode)) return;
            ecomTakenByDiv[divCode] = (ecomTakenByDiv.TryGetValue(divCode, out var t) ? t : 0) + qty;
        }

        // OTS PO Allocation run validation — same behaviour as before, now after wave 1.
        var otsRunByKey  = new Dictionary<(string StoreID, int DivCode), OtsRunLookupRow>();
        var runningOtsQty = new Dictionary<(string StoreID, int DivCode), int>();
        if (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers)
        {
            // The query is now pinned to OTSDate = today, so "no rows" means today's
            // run is missing rather than the month's — say so, since that is the
            // action the operator has to take.
            if (otsRunRowsList.Count == 0)
                throw new InvalidOperationException(
                    $"{runOption} needs an OTS for PO Allocation run generated TODAY ({nowGst:dd/MM/yyyy} GST) " +
                    "in the picked Allocation Countries with TgtEOM > 50. Go to OTS for PO Allocation → Generate, then re-run Process.");
            // Last-write-wins here is only safe because the source is one run per
            // (OTSDate, StoreID, DivCode). Concurrent scheduled instances broke that
            // on 2026-08-21 — two OtsWeekly runs a second apart left duplicate rows,
            // and this loop then picked between them arbitrarily, so the same
            // container allocated differently on identical inputs.
            //
            // Take the row with the HIGHEST OtsQtyToday on a tie so the choice is at
            // least deterministic, and count the collisions so a corrupted OTS table
            // is visible rather than silently changing allocation.
            var duplicateKeys = 0;
            foreach (var r in otsRunRowsList)
            {
                var key = (r.StoreID, r.DivCode);
                if (otsRunByKey.TryGetValue(key, out var existing))
                {
                    duplicateKeys++;
                    if (r.OtsQtyToday <= existing.OtsQtyToday) continue;
                }
                otsRunByKey[key] = r;
                runningOtsQty[key] = r.OtsQtyToday;
            }
            if (duplicateKeys > 0)
                Console.Error.WriteLine(
                    $"[ContainerAllocation] WARN: {duplicateKeys} duplicate (StoreID, DivCode) row(s) in " +
                    $"WmsOtsPoAllocationRun for OTSDate {nowGst:yyyy-MM-dd} — more than one OTS run persisted " +
                    "for today. Re-run Generate to clear them.");
        }

        var distinctDivs = divByItem.Values.Where(d => d > 0).Distinct().ToArray();
        if (distinctDivs.Length == 0) return new(result, blocked, trace);
        var completedContnos = completed.Select(x => x.ContNo).Distinct().ToArray();

        // ================= Wave 2 =================
        progress?.Report(new AllocationProgress(0, 0, "Prefetching: wave 2 (4 lookups in parallel)"));

        async Task<List<(string StoreID, string Country, int DivCode, string VolumeGroup, int MerchNeedMonth)>> LoadEomStores()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreID, string Country, int DivCode, string VolumeGroup, int MerchNeedMonth)>(
                new CommandDefinition(@"
                    SELECT s.StoreID, s.Country, s.DivCode, s.VolumeGroup,
                           ISNULL(s.MerchNeedMonth, 0) AS MerchNeedMonth
                    FROM dbo.LPM_EOM_Output s WITH (NOLOCK)
                    WHERE s.DivCode IN @divs
                      AND s.Month1  = MONTH(DATEADD(hour, 4, SYSUTCDATETIME()))
                      AND s.Year1   = YEAR(DATEADD(hour, 4, SYSUTCDATETIME()))
                      AND s.VolumeGroup IS NOT NULL
                      AND (@noCountryFilter = 1 OR s.Country IN @countries)",
                    new { divs = distinctDivs,
                          noCountryFilter = hasCountryFilter ? 0 : 1,
                          countries = countryFilter },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        async Task<List<(string Country, int DivCode, string GroupCode, int WHStockFrom, int WHStockTo, int SKUMax)>> LoadRulesRaw()
        {
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string Country, int DivCode, string GroupCode, int WHStockFrom, int WHStockTo, int SKUMax)>(
                new CommandDefinition(@"
                    SELECT Country, DivCode, GroupCode, WHStockFrom, WHStockTo, SKUMax
                    FROM dbo.LPM_SKUMaxRule WITH (NOLOCK)
                    WHERE DivCode IN @divs AND IsActive = 1",
                    new { divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        async Task<List<(string StoreID, int DivCode, int targetEOM, int SOH, int SalesTgtWk, int trfSum)>> LoadOtsRaw()
        {
            // FillSKUMax+RoundRobin sources OTS from WmsOtsPoAllocationRun (Live OTS%)
            // and never touches LPM_OTS_Output — skip the query entirely for that run
            // option so the on-prem view isn't hit for nothing.
            if (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers) return new();
            await using var c1 = OpenOnPremBackup();
            return (await c1.QueryAsync<(string StoreID, int DivCode, int targetEOM, int SOH, int SalesTgtWk, int trfSum)>(
                new CommandDefinition(@"
                    WITH ranked AS (
                        SELECT o.StoreID, o.DivCode,
                               ISNULL(o.targetEOM,0)   AS targetEOM,
                               ISNULL(o.SOH,0)         AS SOH,
                               ISNULL(o.SalesTgtWk,0)  AS SalesTgtWk,
                               ISNULL(o.trfQty1,0) + ISNULL(o.trfqty2,0) + ISNULL(o.trfqty3,0)
                                 + ISNULL(o.trfqty4,0) + ISNULL(o.trfqty5,0) + ISNULL(o.trfqty6,0)
                                 + ISNULL(o.trfqty7,0)                            AS trfSum,
                               ROW_NUMBER() OVER (PARTITION BY o.StoreID, o.DivCode
                                                  ORDER BY o.OTSDate DESC) AS rn
                        FROM dbo.LPM_OTS_Output o WITH (NOLOCK)
                        WHERE o.DivCode IN @divs
                          AND o.OTSDate < CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE)
                    )
                    SELECT StoreID, DivCode, targetEOM, SOH, SalesTgtWk, trfSum
                      FROM ranked WHERE rn = 1",
                    new { divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        // Fill SKUMAX + RR only: per-(StoreID, DivCode) VolumeGroup lookup from
        // StoreDivGrade (the "Generate Volume Group" output). Stores/divs missing
        // here become Blocked "No VolumeGroup" during the allocation loop.
        async Task<Dictionary<(string StoreID, int DivCode), string>> LoadStoreDivGrade()
        {
            var d = new Dictionary<(string, int), string>();
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers) return d;
            await using var c1 = OpenOnPremBackup();
            var rows = await c1.QueryAsync<(string StoreID, int DivCode, string Grade)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, Grade
                  FROM LPMSIM.dbo.StoreDivGrade WITH (NOLOCK)
                 WHERE Month1 = @m AND Year1 = @y
                   AND Grade IS NOT NULL AND Grade <> ''",
                new { m = nowGst.Month, y = nowGst.Year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows) d[(r.StoreID, r.DivCode)] = r.Grade;
            return d;
        }

        // Fill SKUMAX + RR only: per-(DivCode, VolumeGroup) SKUMax tier bands.
        // The band whose PoQtyFrom..PoQtyTo contains the item's PoQty is picked
        // per-item; the tier (MinMin/MinMax/IdealMax/MaxMax) then picked per-store
        // based on that store's OTS% vs AvgOTS +/- band. Missing (Div, VG) or
        // PoQty out of any band -> Blocked "No SkuMax band" at pick time.
        //
        // Country = 'BFLGROUP' is REQUIRED, not incidental. LPM_SkuMaxBands holds
        // per-country rows too, and the dictionary below is keyed only by
        // (DivCode, VolumeGroup) — so without this filter every country's bands for
        // the same (Div, VG) piled into one list and the "first row that contains
        // PoQty wins" pick took whichever one SQL happened to return first.
        // Observed on AEINT8194 item 5715356700232 (DivCode 410, PoQty 500): every
        // A store capped at 9 — the MinMin of some other country's row — instead of
        // 4 from BFLGROUP's 251-500 band, so the container drained twice as fast as
        // intended. BFLGROUP is the live config; the per-country rows are stale.
        //
        // UPPER(LTRIM(RTRIM(...))) because this table's Country is hand-maintained
        // and mixed case ('BFLGroup', 'Oman') — the same casing that starved four
        // countries in the ADM band lookup.
        //
        // ORDER BY PoQtyFrom makes the first-match pick deterministic even if two
        // BFLGROUP rows ever overlap.
        async Task<Dictionary<(int DivCode, string VG), List<(int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>>> LoadSkuMaxBands()
        {
            var d = new Dictionary<(int, string), List<(int, int, int?, int?, int?, int?)>>();
            if (runOption != RunOption.FillSKUMaxRoundRobin && runOption != RunOption.FillMinMinPlusOthers || distinctDivs.Length == 0) return d;
            await using var c1 = OpenOnPremBackup();
            var rows = await c1.QueryAsync<(int DivCode, string VolumeGroup, int PoQtyFrom, int PoQtyTo, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax)>(
                new CommandDefinition(@"
                    SELECT DivCode, VolumeGroup, PoQtyFrom, PoQtyTo, MinMin, MinMax, IdealMax, MaxMax
                      FROM LPMSIM.dbo.LPM_SkuMaxBands WITH (NOLOCK)
                     WHERE DivCode IN @divs
                       AND IsActive = 1
                       AND UPPER(LTRIM(RTRIM(Country))) = 'BFLGROUP'
                     ORDER BY DivCode, VolumeGroup, PoQtyFrom",
                    new { divs = distinctDivs },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows)
            {
                var key = (r.DivCode, r.VolumeGroup ?? "");
                if (!d.TryGetValue(key, out var list)) { list = new(); d[key] = list; }
                list.Add((r.PoQtyFrom, r.PoQtyTo, r.MinMin, r.MinMax, r.IdealMax, r.MaxMax));
            }
            return d;
        }

        async Task<List<(string ContNo, string Country, string StoreID, string Itemcode, int? Qty)>> LoadApprovedRows()
        {
            await using var c1 = OpenOnPremBackup();
            var excludeClause = completedContnos.Length > 0
                ? "AND d.TcmContno NOT IN @excluded"
                : "";
            var approvedSql = $@"
                SELECT d.TcmContno AS ContNo, d.Country, d.StoreID, d.Itemcode, d.AllocatedQty AS Qty
                  FROM LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK)
                  JOIN LPMSIM.dbo.WMS_ContAllocationData d   WITH (NOLOCK) ON d.BatchNo = h.BatchNo
                 WHERE h.ApprovedDt IS NOT NULL
                   {excludeClause}";
            return (await c1.QueryAsync<(string ContNo, string Country, string StoreID, string Itemcode, int? Qty)>(new CommandDefinition(
                approvedSql,
                new { excluded = completedContnos },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        }

        var w2_eomStores       = LoadEomStores();
        var w2_rulesRaw        = LoadRulesRaw();
        var w2_otsRaw          = LoadOtsRaw();
        var w2_approved        = LoadApprovedRows();
        var w2_storeDivGrade   = LoadStoreDivGrade();
        var w2_skuMaxBands     = LoadSkuMaxBands();

        await Task.WhenAll(w2_eomStores, w2_rulesRaw, w2_otsRaw, w2_approved,
                           w2_storeDivGrade, w2_skuMaxBands);

        var eomStores        = await w2_eomStores;
        var rulesRaw         = await w2_rulesRaw;
        var otsRaw           = await w2_otsRaw;
        var approvedRows     = await w2_approved;
        var storeDivGrade    = await w2_storeDivGrade;
        var skuMaxBandsByKey = await w2_skuMaxBands;

        // Overlay VolumeGroup on the OTS run rows from StoreDivGrade so all
        // downstream Fill SKUMAX + RR logic (priority sort, band lookup) uses
        // the current-month grade. Stores missing from StoreDivGrade get
        // VolumeGroup=null so the eligible loop can add them to Blocked with
        // reason "No VolumeGroup".
        if (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers)
        {
            foreach (var r in otsRunRowsList)
            {
                r.VolumeGroup = storeDivGrade.TryGetValue((r.StoreID, r.DivCode), out var g) ? g : null;
            }
        }

        var storesByDiv = eomStores.GroupBy(s => s.DivCode).ToDictionary(g => g.Key, g => g.ToList());
        var rulesByKey  = rulesRaw
            .GroupBy(r => (r.Country, r.DivCode, r.GroupCode))
            .ToDictionary(g => g.Key, g => g.ToList());
        var otsRawByKey = otsRaw.ToDictionary(o => (o.StoreID, o.DivCode),
            o => (o.targetEOM, o.SOH, o.SalesTgtWk, o.trfSum));

        // Running allocation total per (StoreID, DivCode) this batch — drives the
        // OTS refresh between items. Starts at zero; grows as we allocate.
        var runningAlloc = new Dictionary<(string StoreID, int DivCode), int>();

        // ============ prevAllocatedSeed — built from wave-2 approvedRows ============
        // Any approved-batch item whose DivCode we don't already have from itemMeta
        // needs a small extra vupc_subclass lookup (usually 0 rows).
        var prevAllocatedSeed = new Dictionary<(string StoreID, int DivCode), int>();
        if (approvedRows.Count > 0)
        {
            var extraItems = approvedRows.Select(r => r.Itemcode)
                .Where(i => !divByItem.ContainsKey(i)).Distinct().ToArray();
            var divByApprovedItem = new Dictionary<string, int>(divByItem, StringComparer.OrdinalIgnoreCase);
            if (extraItems.Length > 0)
            {
                await using var c1 = OpenOnPremBackup();
                var extraDivs = await c1.QueryAsync<(string itemcode, int? DivID)>(new CommandDefinition(@"
                    SELECT itemcode, MAX(DivID) AS DivID
                    FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
                    WHERE itemcode IN @items GROUP BY itemcode",
                    new { items = extraItems }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var r in extraDivs) divByApprovedItem[r.itemcode] = r.DivID ?? 0;
            }

            foreach (var r in approvedRows)
            {
                if (completed.Contains((r.ContNo, r.Country))) continue;
                if (!divByApprovedItem.TryGetValue(r.Itemcode, out var div) || div == 0) continue;
                var key = (r.StoreID, div);
                prevAllocatedSeed[key] = prevAllocatedSeed.GetValueOrDefault(key, 0) + (r.Qty ?? 0);
            }
        }

        double? ComputeOts(string sid, int div)
        {
            if (!otsRawByKey.TryGetValue((sid, div), out var raw) || raw.targetEOM == 0) return null;
            var alloc = runningAlloc.GetValueOrDefault((sid, div), 0);
            var prev  = prevAllocatedSeed.GetValueOrDefault((sid, div), 0);
            var numerator = (raw.targetEOM - raw.SOH + raw.SalesTgtWk - alloc - prev) - raw.trfSum;
            return numerator * 1.0 / raw.targetEOM;
        }

        // 3. P3 allocation loop — group lines by (OraPONo, Division, LPMDt) combo, sort
        // items within each combo by Qty DESC, and walk stores by current OTS DESC.
        // After each item finishes, runningAlloc is mutated so the next item's store
        // ordering uses a refreshed OTS reflecting what's already been given out.
        progress?.Report(new AllocationProgress(0, lines.Count, null));
        var idxLine = 0;
        var combos = lines
            .Where(l => l.Qty > 0)
            .Select(l => new
            {
                Line = l,
                Division = itemMeta.TryGetValue(l.ItemCode, out var im) ? im.Division : null
            })
            .GroupBy(x => (x.Line.OraPONo, Division: x.Division ?? "", x.Line.LPMDt))
            .OrderBy(g => g.Key.OraPONo).ThenBy(g => g.Key.Division).ThenBy(g => g.Key.LPMDt);

        foreach (var combo in combos)
        {
            var comboLines = combo.OrderByDescending(x => x.Line.Qty).Select(x => x.Line).ToList();
            foreach (var line in comboLines)
            {
                idxLine++;
                progress?.Report(new AllocationProgress(idxLine, lines.Count, line.ItemCode));
                if (line.Qty <= 0) continue;
                if (!divByItem.TryGetValue(line.ItemCode, out var divCode) || divCode == 0) continue;
                if (!storesByDiv.TryGetValue(divCode, out var divStores)) continue;

                itemMeta.TryGetValue(line.ItemCode, out var itemRow);
                orgByItem.TryGetValue(line.ItemCode, out var orgRow);
                var dept = (itemRow.Department ?? "").Trim();

                // Resolve (StoreID -> band-matching SKUMax + current OTS) for this item.
                var perStore = new Dictionary<string, (string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax, double? Ots)>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in divStores)
                {
                    if (perStore.ContainsKey(s.StoreID)) continue;
                    if (!rulesByKey.TryGetValue((s.Country, divCode, s.VolumeGroup), out var bands)) continue;
                    int? skuMax = null;
                    foreach (var b in bands)
                    {
                        if (line.Qty >= b.WHStockFrom && line.Qty <= b.WHStockTo) { skuMax = b.SKUMax; break; }
                    }
                    if (skuMax is null) continue;
                    perStore[s.StoreID] = (s.Country, s.VolumeGroup, s.MerchNeedMonth, skuMax.Value, ComputeOts(s.StoreID, divCode));
                }

                // Apply block filter (DeptAccess / DivAccess). Surviving stores then sorted
                // by current OTS DESC (nulls last).
                var stores = new List<(string StoreID, string Country, string VolumeGroup, int MerchNeedMonth, int SKUMax, double? Ots)>(perStore.Count);
                foreach (var (storeId, info) in perStore)
                {
                    var sidU  = storeId.Trim().ToUpperInvariant();
                    var deptU = dept.ToUpperInvariant();
                    var deptHit = !string.IsNullOrEmpty(dept) && deptBlocks.Contains((sidU, divCode, deptU));
                    var divHit  = divBlocks.Contains((sidU, divCode));
                    if (deptHit || divHit)
                    {
                        var reason = deptHit && divHit ? "DeptAccess+DivAccess" : (deptHit ? "DeptAccess" : "DivAccess");
                        storeNameById.TryGetValue(storeId, out var sName);
                        blocked.Add(new BlockedItemRow(
                            Contno: line.ContNo, ItemCode: line.ItemCode, ItemName: orgRow.itemname,
                            Division: itemRow.Division, Department: itemRow.Department,
                            StoreID: storeId, StoreName: sName, Country: info.Country,
                            PoQty: line.Qty, DivCode: divCode, BlockReason: reason));
                        continue;
                    }
                    stores.Add((storeId, info.Country, info.VolumeGroup, info.MerchNeedMonth, info.SKUMax, info.Ots));
                }
                if (stores.Count == 0) continue;

                stores = stores
                    .OrderBy(s => s.Ots.HasValue ? 0 : 1)
                    .ThenByDescending(s => s.Ots)
                    .ToList();

                // Build an enrichment-aware row factory. PalletType is season-driven
                // (W → PalletTypeW, else PalletTypeS); SalesPrice is per store country.
                var season = (orgRow.season ?? "").Trim();
                var isWinter = season.Equals("W", StringComparison.OrdinalIgnoreCase);
                string? PalletFor(string sid)
                {
                    if (!palletByStore.TryGetValue(sid, out var pt)) return null;
                    return isWinter ? pt.PalletTypeW : pt.PalletTypeS;
                }

                AllocationRow MakeRow(string sid, string country, string vg, int merch, int cap, int take, int rrExtra)
                {
                    storeNameById.TryGetValue(sid, out var storeName);
                    pricesByCountryItem.TryGetValue((country, line.ItemCode), out var price);
                    var priority    = priorityByStoreDiv.TryGetValue((sid, divCode), out var pr) ? pr : null;
                    var mnw         = mnwByStoreDiv.TryGetValue((sid, divCode), out var mv) ? mv : null;
                    var otsQtyToday = otsRunByKey.TryGetValue((sid, divCode), out var otsRun) ? (int?)otsRun.OtsQtyToday : null;
                    return new AllocationRow(
                        Contno: line.ContNo, OraPONo: line.OraPONo, ItemCode: line.ItemCode,
                        ItemName: orgRow.itemname, Brand: orgRow.vendor, PoQty: line.Qty,
                        StoreID: sid, StoreName: storeName, Country: country, Division: itemRow.Division,
                        VolumeGroup: vg, SkuMax: cap, AllocQty: take, MerchNeedMonth: merch,
                        DivCode: divCode, RoundRobinExtra: rrExtra, LPM: line.LPM, LPMDt: line.LPMDt,
                        OTS: ComputeOts(sid, divCode),
                        Season: orgRow.season, Style: orgRow.Style, Size: orgRow.Size,
                        Department: itemRow.Department, SalesPrice: price,
                        PalletType: PalletFor(sid),
                        PrevAllocatedQty: prevAllocatedSeed.GetValueOrDefault((sid, divCode), 0),
                        PriorityRank: priority,
                        MnwToday: mnw,
                        OtsQtyToday: otsQtyToday);
                }

                var allocs = new Dictionary<string, AllocationRow>(StringComparer.OrdinalIgnoreCase);
                var remaining = line.Qty;

                if (runOption == RunOption.FillSKUMax)
                {
                    foreach (var s in stores)
                    {
                        if (remaining <= 0) break;
                        var take = Math.Min(s.SKUMax, remaining);
                        if (take <= 0) continue;
                        allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, take, 0);
                        remaining -= take;
                    }
                }
                else if (runOption == RunOption.FillSKUMaxRoundRobin || runOption == RunOption.FillMinMinPlusOthers)
                {
                    // ============ OTS-run-based algorithms (FSMRR + FillMinMinPlusOthers) ============
                    // Shared setup (eligible filter, blocks, ECOM Pass 1a, avg calc, VG rank,
                    // tier picker, row factory, sort helpers) is IDENTICAL between the two
                    // algorithms. Only the 4-pass logic below diverges.
                    //
                    // Store universe: WmsOtsPoAllocationRun for current Month/Year,
                    // filtered by Allocation Countries and this item's DivCode.
                    // No PriorityRank filter. Item Division must match store DivCode.
                    // ECOM Manual Alloc gate is checked upstream in ValidateAsync.
                    var eligible = otsRunByKey.Values
                        .Where(r => r.DivCode == divCode)
                        .ToList();

                    // Hard block: any (Store, Item) whose LPM_SimItemSkuMax.SkuMax
                    // = 0 is not eligible for allocation for this item. Excluded
                    // from every pass AND added to the Blocked Items list so the
                    // operator can see the shortfall.
                    // (Rows with SimItemSkuMax > 0 or no row at all proceed through
                    // the LPM_SkuMaxBands tier lookup.)
                    var itemKey = line.ItemCode.ToUpperInvariant();
                    bool IsSimSkuBlocked(string sid) =>
                        simSkuMaxBlocked.Contains((sid.ToUpperInvariant(), itemKey));
                    var simBlockedStores = eligible.Where(r => IsSimSkuBlocked(r.StoreID)).ToList();
                    foreach (var r in simBlockedStores)
                    {
                        eligible.Remove(r);
                        storeNameById.TryGetValue(r.StoreID, out var sName);
                        blocked.Add(new BlockedItemRow(
                            Contno: line.ContNo, ItemCode: line.ItemCode, ItemName: orgRow.itemname,
                            Division: itemRow.Division, Department: itemRow.Department,
                            StoreID: r.StoreID, StoreName: sName, Country: r.Country,
                            PoQty: line.Qty, DivCode: divCode, BlockReason: "LPM_SimItemSkuMax.SkuMax=0"));
                    }

                    // Hard block: stores without a VolumeGroup in StoreDivGrade for
                    // the current Month/Year. Operator must Generate Volume Group
                    // first to cover them.
                    var noVgStores = eligible.Where(r => string.IsNullOrWhiteSpace(r.VolumeGroup)).ToList();
                    foreach (var r in noVgStores)
                    {
                        eligible.Remove(r);
                        storeNameById.TryGetValue(r.StoreID, out var sName);
                        blocked.Add(new BlockedItemRow(
                            Contno: line.ContNo, ItemCode: line.ItemCode, ItemName: orgRow.itemname,
                            Division: itemRow.Division, Department: itemRow.Department,
                            StoreID: r.StoreID, StoreName: sName, Country: r.Country,
                            PoQty: line.Qty, DivCode: divCode, BlockReason: "No VolumeGroup (StoreDivGrade)"));
                    }

                    // Hard block: (DivCode, VolumeGroup) has no LPM_SkuMaxBands row
                    // whose PoQtyFrom..PoQtyTo contains this item's PoQty.
                    bool HasBand(OtsRunLookupRow r)
                    {
                        if (!skuMaxBandsByKey.TryGetValue((r.DivCode, r.VolumeGroup ?? ""), out var bands)) return false;
                        foreach (var b in bands)
                            if (line.Qty >= b.From && line.Qty <= b.To) return true;
                        return false;
                    }
                    var noBandStores = eligible.Where(r => !HasBand(r)).ToList();
                    foreach (var r in noBandStores)
                    {
                        eligible.Remove(r);
                        storeNameById.TryGetValue(r.StoreID, out var sName);
                        blocked.Add(new BlockedItemRow(
                            Contno: line.ContNo, ItemCode: line.ItemCode, ItemName: orgRow.itemname,
                            Division: itemRow.Division, Department: itemRow.Department,
                            StoreID: r.StoreID, StoreName: sName, Country: r.Country,
                            PoQty: line.Qty, DivCode: divCode, BlockReason: "No SkuMax band (LPM_SkuMaxBands)"));
                    }

                    // ECOM Manual Priority — when the operator ticks the checkbox on
                    // the page, WmsManualAllocation.AllocationQty for (ONLINE, itemcode)
                    // is honoured before any OTS-based pass runs, and ONLINE takes
                    // NOTHING beyond it. Cap = min(manual qty, remaining), tagged as
                    // Pass1Qty so no schema change is needed.
                    //
                    // ONLINE is removed from `eligible` for EVERY item while the toggle
                    // is on — not only for items that happen to have a manual row.
                    // Previously the removal sat inside the "took something" branch, so
                    // on items with no manual figure ONLINE stayed in the running and
                    // won stock through Passes 1b-4 like any other store. On AEINT8070
                    // that made ECOM 1,760 against a manual total of 760.
                    //
                    // The point of setting a manual number is to control ECOM's take,
                    // so the toggle now means exactly that: ECOM gets the manual qty
                    // and the rest of the container goes to the other countries.
                    OtsRunLookupRow? ecomOtsRow = null;
                    int ecomPreAllocTake = 0;
                    // The manual figure itself, kept separately so the trace row can
                    // show the cap even when `remaining` clipped the take (or left
                    // nothing to take at all).
                    int ecomManualCap = 0;
                    if (ecomManualPriority)
                    {
                        // Capture the OTS row before removing, so the Pass 1a trace can
                        // still report ONLINE's VolumeGroup / LiveOtsPct.
                        ecomOtsRow = eligible.FirstOrDefault(r => string.Equals(r.StoreID, "ONLINE", StringComparison.OrdinalIgnoreCase));
                        eligible.RemoveAll(r => string.Equals(r.StoreID, "ONLINE", StringComparison.OrdinalIgnoreCase));

                        if (!IsSimSkuBlocked("ONLINE")
                            && initialAllocByKey.TryGetValue(("ONLINE", line.ItemCode.ToUpperInvariant()), out var ecomManualQty)
                            && ecomManualQty > 0)
                        {
                            ecomManualCap = ecomManualQty;
                            // The container budget binds the manual figure too — an
                            // operator's manual qty is a request, not an override of
                            // the country's PO-share ceiling.
                            ecomPreAllocTake = Math.Min(Math.Min(ecomManualQty, remaining),
                                                        EcomRemainingAllowance(divCode));
                        }
                    }

                    // NB: deliberately still keyed on ecomPreAllocTake, not ecomManualCap.
                    // Letting a manual qty that took nothing keep the item alive here
                    // would push it into Pass 4 and flag it — a behaviour change, where
                    // this work is audit-only.
                    if (eligible.Count == 0 && ecomPreAllocTake == 0) continue;

                    // Live OTS% per (StoreID, DivCode) driven by runningOtsQty
                    // (decreases as we allocate). Used by Passes 2, 3, 4.
                    // Denominator = CurrentEOW (matches OtsPercentToday's
                    // formula on the OTS PO Allocation report). Falls back to
                    // TgtEOM only when CurrentEOW is unavailable (older runs
                    // before the CurrentEOW column was added).
                    double LiveOtsPct(OtsRunLookupRow r)
                    {
                        var qty = runningOtsQty.GetValueOrDefault((r.StoreID, r.DivCode), r.OtsQtyToday);
                        var denom = r.CurrentEOW > 0 ? r.CurrentEOW : r.TgtEOM;
                        return denom > 0 ? (double)qty / denom * 100.0 : 0.0;
                    }

                    // Static OTS% straight from WmsOtsPoAllocationRun.OtsPercentToday.
                    // Doesn't change during this batch — Pass 1's filter and sort
                    // both use this per the current spec.
                    double StaticOtsPct(OtsRunLookupRow r) => (double)r.OtsPercentToday;

                    // Avg OtsPercentToday for THIS Division from stores where
                    // OtsPercentToday > 0. This is Pass 1's threshold.
                    // (ONLINE already removed above if ECOM Manual Priority was
                    // applied; cap-0 stores also removed above.)
                    var positives = eligible.Select(StaticOtsPct).Where(p => p > 0).ToList();
                    var avgOts = positives.Count > 0 ? positives.Average() : 0.0;
                    var avgOtsDecimal = (decimal)Math.Round(avgOts, 2);
                    // IdealMax band edges for this item (stamped on every row for audit).
                    var avgOtsMinDecimal = (decimal)Math.Round(avgOts - otsBandPct, 2);
                    var avgOtsMaxDecimal = (decimal)Math.Round(avgOts + otsBandPct, 2);

                    // Materialise the ECOM pre-alloc row now that avgOtsDecimal is known.
                    if (ecomPreAllocTake > 0)
                    {
                        AllocationRow ecomRow;
                        if (ecomOtsRow != null)
                        {
                            ecomRow = BumpRow(null, ecomOtsRow, ecomPreAllocTake, 0, pass: 1);
                        }
                        else
                        {
                            // ONLINE isn't in the OTS PO Allocation run for this DivCode —
                            // build the row directly. Country label is hard-coded ECOM to
                            // match the store universe convention.
                            // ONLINE isn't in the OTS PO Allocation run for this DivCode so
                            // we have no TgtEOM/LiveOtsPct to stamp — leave OTS/TgtEOM null.
                            ecomRow = MakeRow("ONLINE", "ECOM", "", 0, ecomPreAllocTake, ecomPreAllocTake, 0)
                                with { Pass1Qty = ecomPreAllocTake, AvgOtsPercent = avgOtsDecimal, OTS = null };
                            // This branch skips BumpRow, so the division budget has to
                            // be charged here or ECOM would overrun the cap on exactly
                            // the items missing from the OTS run.
                            SpendEcom(divCode, ecomPreAllocTake);
                        }
                        allocs["ONLINE"] = ecomRow;
                        remaining -= ecomPreAllocTake;
                    }

                    // Pass 1a trace. Direct append rather than RecordTrace because
                    // ONLINE may have no row in the OTS run for this DivCode, and
                    // RecordTrace requires an OtsRunLookupRow to read VolumeGroup /
                    // LiveOtsPct / InitialOtsPct from. Same approach as the Pass 4
                    // "Flagged" synthetic row below.
                    //
                    // Without this the trace was missing ECOM entirely, so
                    // SUM(Take) per (ContNo, Itemcode) no longer reconciled to
                    // line.Qty whenever ECOM Manual Priority was on.
                    //
                    // Cap carries the manual figure, so a take clipped by `remaining`
                    // is visible as Cap > Take. A manual qty that could take nothing
                    // records a Take=0 / CapReached row, matching passes 1b-4.
                    if (trace is not null && ecomManualCap > 0)
                    {
                        var ecomSoh = itemSohByStore.GetValueOrDefault(
                            ("ONLINE", line.ItemCode.ToUpperInvariant()), 0);
                        var ecomRunning = ecomOtsRow is not null
                            ? runningOtsQty.GetValueOrDefault((ecomOtsRow.StoreID, ecomOtsRow.DivCode), ecomOtsRow.OtsQtyToday)
                            : 0;
                        var remBeforeEcom = remaining + ecomPreAllocTake;

                        trace.Add(new AllocationTraceRow(
                            ContNo: line.ContNo, Itemcode: line.ItemCode, StoreID: "ONLINE",
                            DivCode: divCode, Pass: 1, SortRank: 0,
                            VolumeGroup: ecomOtsRow?.VolumeGroup,
                            TierName: "EcomManual",
                            LiveOtsPctBefore: ecomOtsRow is not null
                                ? (decimal)Math.Round(LiveOtsPct(ecomOtsRow), 2)
                                : null,
                            Cap: ecomManualCap, Soh: ecomSoh,
                            CurrentBeforeTake: 0,
                            RemainingBefore: remBeforeEcom,
                            Take: ecomPreAllocTake,
                            RemainingAfter: remBeforeEcom - ecomPreAllocTake,
                            RunningOtsQtyAfter: ecomRunning - ecomPreAllocTake,
                            RunOption: runOption.ToString(),
                            SkipReason: ecomPreAllocTake > 0 ? null : "CapReached",
                            DefaultSkuMax: null, RawSkuMax: null, RatioSkuMax: null,
                            AvgOtsPercent: avgOtsDecimal,
                            AvgOtsMin: avgOtsMinDecimal,
                            AvgOtsMax: avgOtsMaxDecimal,
                            InitialOtsPct: ecomOtsRow?.OtsPercentToday,
                            PONo: line.OraPONo,
                            LPMDt: line.LPMDt,
                            Country: ecomOtsRow?.Country ?? "ECOM"));
                    }

                    int VolumeGroupRank(string? vg)
                    {
                        if (string.IsNullOrWhiteSpace(vg)) return 999;
                        return vgSortOrder.TryGetValue(vg.Trim(), out var rank) ? rank : 999;
                    }

                    // Tier-based SKUMax: pick the (DivCode, VG, PoQtyRange) band
                    // whose PoQtyFrom..PoQtyTo contains line.Qty, then pick the
                    // MinMin / MinMax / IdealMax / MaxMax column based on the
                    // store's LiveOtsPct relative to AvgOts +/- OTSBandPct
                    // (percentage points, from WmsAppConfig). Effective cap
                    // subtracts per-(Store, Item) SOH from LPM_locstock.
                    // eligible has already been filtered for "no band" / "no VG";
                    // this call assumes a band exists.
                    (int Raw, int Cap, string TierName) SkuMaxRawAndCapFor(OtsRunLookupRow r)
                    {
                        var bands = skuMaxBandsByKey[(r.DivCode, r.VolumeGroup ?? "")];
                        (int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax) b = default;
                        foreach (var x in bands)
                            if (line.Qty >= x.From && line.Qty <= x.To) { b = x; break; }
                        var ots = LiveOtsPct(r);
                        var (tierValue, tierName) = ots switch
                        {
                            < 0                                      => (b.MinMin, "MinMin"),
                            _ when ots <  avgOts - otsBandPct        => (b.MinMax, "MinMax"),
                            _ when ots <= avgOts + otsBandPct        => (b.IdealMax, "IdealMax"),
                            _                                        => (b.MaxMax, "MaxMax"),
                        };
                        var tier = tierValue ?? 0;
                        var soh = itemSohByStore.GetValueOrDefault(
                            (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                        return (tier, Math.Max(0, tier - soh), tierName);
                    }
                    // ECOM's container budget clamps its per-item cap, so EVERY pass
                    // that sizes a take off CapFor honours it without its own guard.
                    // Pass 4 sizes off ratio shares instead, so it clamps separately.
                    int CapFor(OtsRunLookupRow r)
                    {
                        var cap = SkuMaxRawAndCapFor(r).Cap;
                        return IsEcomStore(r.StoreID)
                            ? Math.Min(cap, EcomRemainingAllowance(r.DivCode))
                            : cap;
                    }

                    // Row factory bound to this item — records which pass emitted
                    // each piece and stamps AvgOtsPercent + LiveOtsPct + TgtEOM +
                    // RawSkuMax (all sourced from WmsOtsPoAllocationRun /
                    // LPM_SKUMaxRule) on every row.
                    AllocationRow BumpRow(AllocationRow? existing, OtsRunLookupRow r,
                                          int delta, int rrExtra, int pass, string? tierNameOverride = null)
                    {
                        var (rawSku, cap, tierName) = SkuMaxRawAndCapFor(r);
                        var effectiveTierName = tierNameOverride ?? tierName;
                        // Single place every pass funnels a take through, so the
                        // container budget is spent here and nowhere else. Callers
                        // have already clamped delta (CapFor, or Pass 4's own guard),
                        // so this only records what was actually taken.
                        if (IsEcomStore(r.StoreID)) SpendEcom(r.DivCode, delta);
                        var soh = itemSohByStore.GetValueOrDefault(
                            (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                        var running = runningOtsQty.GetValueOrDefault((r.StoreID, r.DivCode), r.OtsQtyToday);
                        if (existing is null)
                        {
                            // OTS on the persisted row is clipped at 0 — LiveOtsPct
                            // can go negative once runningOtsQty is over-allocated
                            // (Pass 4 uncapped RR runs even for depleted stores).
                            // Negative values still drive the algorithm's sort key;
                            // only the persisted display value is clipped.
                            var initial = MakeRow(r.StoreID, r.Country,
                                r.VolumeGroup ?? "", 0, cap, delta, rrExtra)
                                with
                                {
                                    AvgOtsPercent = avgOtsDecimal,
                                    OTS = Math.Max(0, LiveOtsPct(r)),
                                    TgtEOM = r.TgtEOM,
                                    RawSkuMax = rawSku,
                                    SkuMaxBand = effectiveTierName,
                                    AvgOtsMin = avgOtsMinDecimal,
                                    AvgOtsMax = avgOtsMaxDecimal,
                                    InitialOtsPct = r.OtsPercentToday,
                                    Soh = soh,
                                    RunningOtsQty = running,
                                };
                            return pass switch
                            {
                                1 => initial with { Pass1Qty = delta },
                                2 => initial with { Pass2Qty = delta },
                                3 => initial with { Pass3Qty = delta },
                                _ => initial with { Pass4Qty = delta, Phase2Qty = (initial.Phase2Qty ?? 0) + delta },
                            };
                        }
                        var upd = existing with
                        {
                            AllocQty = existing.AllocQty + delta,
                            RoundRobinExtra = existing.RoundRobinExtra + rrExtra,
                            AvgOtsPercent = existing.AvgOtsPercent ?? avgOtsDecimal,
                            // Refresh audit fields at the latest pass write.
                            SkuMaxBand = effectiveTierName,
                            AvgOtsMin = existing.AvgOtsMin ?? avgOtsMinDecimal,
                            AvgOtsMax = existing.AvgOtsMax ?? avgOtsMaxDecimal,
                            InitialOtsPct = existing.InitialOtsPct ?? r.OtsPercentToday,
                            Soh = existing.Soh ?? soh,
                            RunningOtsQty = running,
                        };
                        return pass switch
                        {
                            1 => upd with { Pass1Qty = (upd.Pass1Qty ?? 0) + delta },
                            2 => upd with { Pass2Qty = (upd.Pass2Qty ?? 0) + delta },
                            3 => upd with { Pass3Qty = (upd.Pass3Qty ?? 0) + delta },
                            _ => upd with { Pass4Qty = (upd.Pass4Qty ?? 0) + delta,
                                            Phase2Qty = (upd.Phase2Qty ?? 0) + delta },
                        };
                    }

                    // Trace hook — writes one row per Pass touch when the operator
                    // ticked "Trace Allocation" on the razor page. No-ops otherwise.
                    void RecordTrace(int pass, int sortRank, OtsRunLookupRow r, string tierName,
                                     int cap, int currentBefore, int remainingBefore, int take,
                                     string? skipReason = null, int? ratioSkuMaxOverride = null,
                                     int? rawSkuMaxOverride = null)
                    {
                        if (trace is null) return;
                        var soh = itemSohByStore.GetValueOrDefault(
                            (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                        var running = runningOtsQty.GetValueOrDefault((r.StoreID, r.DivCode), r.OtsQtyToday);
                        // RawSkuMax follows THIS row's TierName — Pass 1b uses MinMin,
                        // Pass 3 uses MinMax, Pass 2 uses whatever the OTS picker chose,
                        // Pass 4 uses MinMax. DefaultSkuMax = RawSkuMax - Soh. Callers can
                        // pass rawSkuMaxOverride to force the value (e.g. FMMPO Pass 1b
                        // bypass uses MinMin=1 regardless of the band).
                        int rawTier;
                        if (rawSkuMaxOverride.HasValue)
                        {
                            rawTier = rawSkuMaxOverride.Value;
                        }
                        else
                        {
                            rawTier = 0;
                            if (skuMaxBandsByKey.TryGetValue((r.DivCode, r.VolumeGroup ?? ""), out var bandsForTrace))
                            {
                                foreach (var b in bandsForTrace)
                                {
                                    if (line.Qty < b.From || line.Qty > b.To) continue;
                                    rawTier = tierName switch
                                    {
                                        "MinMin"   => b.MinMin   ?? 0,
                                        "MinMax"   => b.MinMax   ?? 0,
                                        "IdealMax" => b.IdealMax ?? 0,
                                        "MaxMax"   => b.MaxMax   ?? 0,
                                        _          => 0,
                                    };
                                    break;
                                }
                            }
                        }
                        var defaultSkuMax = Math.Max(0, rawTier - soh);
                        // RatioSkuMax on Pass 4 rows carries the ratio DENOMINATOR — the sum
                        // of all eligible stores' raw SKU-max contributions for this item.
                        // Each store's share = RawSkuMax / RatioSkuMax * RemainingBefore, so
                        // stamping the denominator makes the share verifiable from the row.
                        // FMMPO passes the total via override; FSMRR falls back to `cap` (its
                        // per-store tier cap, matching prior behaviour there). NULL for
                        // passes 1b / 2 / 3 — no ratio.
                        int? ratioSkuMax = pass == 4 ? (ratioSkuMaxOverride ?? cap) : (int?)null;
                        trace.Add(new AllocationTraceRow(
                            ContNo: line.ContNo, Itemcode: line.ItemCode, StoreID: r.StoreID,
                            DivCode: divCode, Pass: pass, SortRank: sortRank,
                            VolumeGroup: r.VolumeGroup, TierName: tierName,
                            LiveOtsPctBefore: (decimal)Math.Round(LiveOtsPct(r), 2),
                            Cap: cap, Soh: soh,
                            CurrentBeforeTake: currentBefore,
                            RemainingBefore: remainingBefore,
                            Take: take,
                            RemainingAfter: remainingBefore - take,
                            RunningOtsQtyAfter: running - take,
                            RunOption: runOption.ToString(),
                            SkipReason: skipReason,
                            DefaultSkuMax: defaultSkuMax,
                            RawSkuMax: rawTier,
                            RatioSkuMax: ratioSkuMax,
                            AvgOtsPercent: avgOtsDecimal,
                            AvgOtsMin: avgOtsMinDecimal,
                            AvgOtsMax: avgOtsMaxDecimal,
                            InitialOtsPct: r.OtsPercentToday,
                            PONo: line.OraPONo,
                            LPMDt: line.LPMDt,
                            Country: r.Country));
                    }

                    // Sort order used by Passes 2, 3, 4: VolumeGroup A+ -> E, then LiveOtsPct DESC.
                    IEnumerable<OtsRunLookupRow> SortByGroupThenOts(IEnumerable<OtsRunLookupRow> src) =>
                        src.OrderBy(r => VolumeGroupRank(r.VolumeGroup))
                           .ThenByDescending(LiveOtsPct);

                    // Pass 1 uses OtsPercentToday for both filter and sort.
                    IEnumerable<OtsRunLookupRow> SortByGroupThenStaticOts(IEnumerable<OtsRunLookupRow> src) =>
                        src.OrderBy(r => VolumeGroupRank(r.VolumeGroup))
                           .ThenByDescending(StaticOtsPct);

                    if (runOption == RunOption.FillSKUMaxRoundRobin)
                    {
                        // ---------- Pass 1: OtsPercentToday >= Avg OtsPercentToday (sequential fill up to cap) ----------
                        var pass1Stores = SortByGroupThenStaticOts(eligible.Where(r => StaticOtsPct(r) >= avgOts)).ToList();
                        for (var i = 0; i < pass1Stores.Count; i++)
                        {
                            if (remaining <= 0) break;
                            var r = pass1Stores[i];
                            var (_, cap, tierName) = SkuMaxRawAndCapFor(r);
                            var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                            var remBefore = remaining;
                            var take = Math.Min(cap - current, remaining);
                            if (take <= 0)
                            {
                                RecordTrace(1, i, r, tierName, cap, current, remBefore, 0, skipReason: "CapReached");
                                continue;
                            }
                            allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 1);
                            remaining -= take;
                            RecordTrace(1, i, r, tierName, cap, current, remBefore, take);
                        }

                        // ---------- Pass 2: 0 < OTS% < AvgOTS% (sequential fill up to cap) ----------
                        if (remaining > 0)
                        {
                            var pass2Stores = SortByGroupThenOts(
                                eligible.Where(r => LiveOtsPct(r) > 0 && LiveOtsPct(r) < avgOts)).ToList();
                            for (var i = 0; i < pass2Stores.Count; i++)
                            {
                                if (remaining <= 0) break;
                                var r = pass2Stores[i];
                                var (_, cap, tierName) = SkuMaxRawAndCapFor(r);
                                var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                var remBefore = remaining;
                                var take = Math.Min(cap - current, remaining);
                                if (take <= 0)
                                {
                                    RecordTrace(2, i, r, tierName, cap, current, remBefore, 0, skipReason: "CapReached");
                                    continue;
                                }
                                allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 2);
                                remaining -= take;
                                RecordTrace(2, i, r, tierName, cap, current, remBefore, take);
                            }
                        }

                        // ---------- Pass 3: OTS% <= 0 (round-robin up to cap) ----------
                        if (remaining > 0)
                        {
                            var pass3Stores = SortByGroupThenOts(eligible.Where(r => LiveOtsPct(r) <= 0)).ToList();
                            // Only log a CapReached trace row once per (store, this pass) — the RR
                            // outer while can visit the same capped store many times.
                            var pass3SkipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            while (remaining > 0 && pass3Stores.Count > 0)
                            {
                                bool any = false;
                                for (var i = 0; i < pass3Stores.Count; i++)
                                {
                                    if (remaining <= 0) break;
                                    var r = pass3Stores[i];
                                    var (_, cap, tierName) = SkuMaxRawAndCapFor(r);
                                    var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                    if (current >= cap)
                                    {
                                        if (pass3SkipLogged.Add(r.StoreID))
                                            RecordTrace(3, i, r, tierName, cap, current, remaining, 0, skipReason: "CapReached");
                                        continue;
                                    }
                                    var remBefore = remaining;
                                    allocs[r.StoreID] = BumpRow(row, r, 1, 0, pass: 3);
                                    remaining--;
                                    any = true;
                                    RecordTrace(3, i, r, tierName, cap, current, remBefore, 1);
                                }
                                if (!any) break;
                            }
                        }

                        // ---------- Pass 4: RATIO distribution across eligible stores ----------
                        // Refresh tier cap per store using LiveOtsPct (already reflects
                        // Passes 1-3 allocations via runningOtsQty). Distribute the
                        // leftover proportionally to each store's cap so bigger stores
                        // absorb bigger overflow shares. Cap-0 stores get nothing.
                        // Store's RatioSkuMax column captures the numerator so
                        // operators can verify take_i = round(remaining * cap_i / SUM).
                        if (remaining > 0)
                        {
                            var pass4Stores = SortByGroupThenOts(eligible)
                                .Select(r => (Row: r, Cap: CapFor(r)))
                                .Where(x => x.Cap > 0)
                                .ToList();
                            var totalCap = pass4Stores.Sum(x => x.Cap);
                            if (totalCap > 0)
                            {
                                var origRemaining = remaining;
                                for (int i = 0; i < pass4Stores.Count && remaining > 0; i++)
                                {
                                    var (r, cap) = pass4Stores[i];
                                    var take = i == pass4Stores.Count - 1
                                        ? remaining
                                        : (int)Math.Floor((double)origRemaining * cap / totalCap);
                                    var existingRow = allocs.TryGetValue(r.StoreID, out var row) ? row : null;
                                    var current = existingRow?.AllocQty ?? 0;
                                    var remBefore = remaining;
                                    if (take <= 0)
                                    {
                                        RecordTrace(4, i, r, SkuMaxRawAndCapFor(r).TierName, cap, current, remBefore, 0, skipReason: "ShareZero");
                                        continue;
                                    }
                                    var newRow = BumpRow(existingRow, r, take, 0, pass: 4)
                                        with { RatioSkuMax = cap };
                                    allocs[r.StoreID] = newRow;
                                    remaining -= take;
                                    RecordTrace(4, i, r, SkuMaxRawAndCapFor(r).TierName, cap, current, remBefore, take);
                                }
                            }
                        }
                    }
                    else  // FillMinMinPlusOthers
                    {
                        // ================ FillMinMinPlusOthers passes ================
                        // Pass 1b: fill every A..H store up to (MinMin - SOH), positive OTS only, grade asc + OTS desc.
                        // Pass 2 : top positive-OTS stores up from current alloc to their OTS-driven tier cap (- SOH).
                        // Pass 3 : negative-OTS stores get up to (MinMin - SOH). Conservative
                        //          — over-stocked stores get only the safety floor.
                        // Pass 4 : if remaining < 10% of PoQty -> distribute across A/B/C
                        //          stores with LiveOts > 0, proportional to raw MinMax
                        //          (share = MinMax / SUM(MinMax) * remaining, no per-store cap).
                        //          else -> flag the item in dbo.WmsPlanningFlag, drop remaining, move on.
                        static bool IsGradeAtoH(string? vg)
                        {
                            if (string.IsNullOrWhiteSpace(vg)) return false;
                            var c = char.ToUpperInvariant(vg.Trim()[0]);
                            return c >= 'A' && c <= 'H';
                        }
                        int MinMinCapFor(OtsRunLookupRow r)
                        {
                            if (!skuMaxBandsByKey.TryGetValue((r.DivCode, r.VolumeGroup ?? ""), out var bands)) return 0;
                            (int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax) b = default;
                            foreach (var x in bands)
                                if (line.Qty >= x.From && line.Qty <= x.To) { b = x; break; }
                            var tier = b.MinMin ?? 0;
                            var soh = itemSohByStore.GetValueOrDefault(
                                (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                            return Math.Max(0, tier - soh);
                        }
                        int MinMaxCapFor(OtsRunLookupRow r)
                        {
                            if (!skuMaxBandsByKey.TryGetValue((r.DivCode, r.VolumeGroup ?? ""), out var bands)) return 0;
                            (int From, int To, int? MinMin, int? MinMax, int? IdealMax, int? MaxMax) b = default;
                            foreach (var x in bands)
                                if (line.Qty >= x.From && line.Qty <= x.To) { b = x; break; }
                            var tier = b.MinMax ?? 0;
                            var soh = itemSohByStore.GetValueOrDefault(
                                (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                            return Math.Max(0, tier - soh);
                        }
                        int RawMinMaxFor(OtsRunLookupRow r)
                        {
                            if (!skuMaxBandsByKey.TryGetValue((r.DivCode, r.VolumeGroup ?? ""), out var bands)) return 0;
                            foreach (var x in bands)
                                if (line.Qty >= x.From && line.Qty <= x.To) return x.MinMax ?? 0;
                            return 0;
                        }

                        // ---------- Pass 1b: everyone (A..H, LiveOts > 0) up to Min-Min ----------
                        // Bypass Pass 1b (operator toggle on the razor page) — v1.0.348+ logic:
                        //   Compute per-item ABC coverage:
                        //     ABCReqdStock  = Sum over A/B/C stores with LiveOts >= 0 of
                        //                     max(0, Pass-2 tier - SOH).
                        //     MinMinCoverPct = ABCReqdStock / PoQty * 100.
                        //   Branch:
                        //     MinMinCoverPct >= 100  ->  SKIP Pass 1b entirely (ABC need
                        //                                already covers the PO — jump to Pass 2).
                        //     MinMinCoverPct <  100  ->  STAGE 1: top up each ABC/OTS>=0 store
                        //                                to its Pass-2 tier (- SOH). STAGE 2:
                        //                                MinMin=1 sweep across A-H OTS>=0
                        //                                stores for the leftover (ABC stores
                        //                                skip via CapReached).
                        //   Every item's calc is audited in dbo.Pass1ByPass, and the pct is
                        //   stamped on every WMS_ContAllocationData row for the item.
                        //   PoQty threshold is no longer used to decide Pass 1b eligibility.
                        decimal? minMinCoverPct = null;   // stamped on every row for this item, null when bypass is off
                        var bypassSkip = false;
                        var bypassAbcThenMinMin = false;
                        if (bypassPass1b)
                        {
                            var abcSet = eligible
                                .Where(r => (r.VolumeGroup?.Trim().ToUpperInvariant()) is "A" or "B" or "C")
                                .Where(r => LiveOtsPct(r) >= 0)
                                .Select(r =>
                                {
                                    var (rawTier, cap, _) = SkuMaxRawAndCapFor(r);
                                    var soh = itemSohByStore.GetValueOrDefault(
                                        (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                                    return (Row: r, Tier: rawTier, Soh: soh, Cap: cap);
                                })
                                .ToList();
                            var abcMax       = abcSet.Sum(x => x.Tier);
                            var abcSoh       = abcSet.Sum(x => x.Soh);
                            var abcReqdStock = abcSet.Sum(x => x.Cap);
                            minMinCoverPct = line.Qty > 0
                                ? Math.Round((decimal)abcReqdStock * 100m / line.Qty, 2)
                                : 0m;
                            pass1BypassAudit!.Add((
                                PONo: line.OraPONo ?? "",
                                Itemcode: line.ItemCode,
                                PoQty: line.Qty,
                                ABCMax: abcMax,
                                ABCSOH: abcSoh,
                                ABCReqdStock: abcReqdStock,
                                MinMinCoverPct: minMinCoverPct.Value));

                            if (minMinCoverPct.Value >= 100m)
                                bypassSkip = true;                        // ABC need alone covers the PO
                            else
                                bypassAbcThenMinMin = true;               // stage 1 + stage 2
                        }

                        if (bypassSkip)
                        {
                            // No Pass 1b work — remaining rolls straight into Pass 2.
                        }
                        else if (bypassAbcThenMinMin)
                        {
                            // ----- Stage 1: top up A/B/C OTS>=0 stores to Pass-2 tier - SOH -----
                            var stage1Stores = eligible
                                .Where(r => (r.VolumeGroup?.Trim().ToUpperInvariant()) is "A" or "B" or "C")
                                .Where(r => LiveOtsPct(r) >= 0)
                                .OrderBy(r => VolumeGroupRank(r.VolumeGroup))
                                .ThenByDescending(LiveOtsPct)
                                .ToList();
                            for (var i = 0; i < stage1Stores.Count; i++)
                            {
                                if (remaining <= 0) break;
                                var r = stage1Stores[i];
                                var (_, cap, tierName) = SkuMaxRawAndCapFor(r);
                                var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                var remBefore = remaining;
                                var take = Math.Min(cap - current, remaining);
                                if (take <= 0)
                                {
                                    RecordTrace(1, i, r, tierName, cap, current, remBefore, 0, skipReason: "CapReached");
                                    continue;
                                }
                                allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 1, tierNameOverride: tierName);
                                remaining -= take;
                                RecordTrace(1, i, r, tierName, cap, current, remBefore, take);
                            }

                            // ----- Stage 2: MinMin=1 sweep on A-H OTS>=0 for the leftover -----
                            if (remaining > 0)
                            {
                                var stage2Stores = eligible
                                    .Where(r => IsGradeAtoH(r.VolumeGroup) && LiveOtsPct(r) >= 0)
                                    .OrderBy(r => VolumeGroupRank(r.VolumeGroup))
                                    .ThenByDescending(LiveOtsPct)
                                    .ToList();
                                for (var i = 0; i < stage2Stores.Count; i++)
                                {
                                    if (remaining <= 0) break;
                                    var r = stage2Stores[i];
                                    var sohOverride = itemSohByStore.GetValueOrDefault(
                                        (r.StoreID.ToUpperInvariant(), line.ItemCode.ToUpperInvariant()), 0);
                                    var cap = Math.Max(0, 1 - sohOverride);
                                    var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                    var remBefore = remaining;
                                    var take = Math.Min(cap - current, remaining);
                                    if (take <= 0)
                                    {
                                        RecordTrace(1, i, r, "MinMin", cap, current, remBefore, 0, skipReason: "CapReached", rawSkuMaxOverride: 1);
                                        continue;
                                    }
                                    allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 1, tierNameOverride: "MinMin");
                                    remaining -= take;
                                    RecordTrace(1, i, r, "MinMin", cap, current, remBefore, take, rawSkuMaxOverride: 1);
                                }
                            }
                        }
                        else
                        {
                            // ----- Default (bypass off): band MinMin floor for A-H OTS>0 -----
                            var pass1bStores = eligible
                                .Where(r => IsGradeAtoH(r.VolumeGroup) && LiveOtsPct(r) > 0)
                                .OrderBy(r => VolumeGroupRank(r.VolumeGroup))
                                .ThenByDescending(LiveOtsPct)
                                .ToList();
                            for (var i = 0; i < pass1bStores.Count; i++)
                            {
                                if (remaining <= 0) break;
                                var r = pass1bStores[i];
                                var cap = MinMinCapFor(r);
                                var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                var remBefore = remaining;
                                var take = Math.Min(cap - current, remaining);
                                if (take <= 0)
                                {
                                    RecordTrace(1, i, r, "MinMin", cap, current, remBefore, 0, skipReason: "CapReached");
                                    continue;
                                }
                                allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 1, tierNameOverride: "MinMin");
                                remaining -= take;
                                RecordTrace(1, i, r, "MinMin", cap, current, remBefore, take);
                            }
                        }

                        // ---------- Pass 2: positive-OTS stores topped up to OTS-tier cap ----------
                        if (remaining > 0)
                        {
                            var pass2Stores = SortByGroupThenOts(eligible.Where(r => LiveOtsPct(r) >= 0)).ToList();
                            for (var i = 0; i < pass2Stores.Count; i++)
                            {
                                if (remaining <= 0) break;
                                var r = pass2Stores[i];
                                var (_, tierCap, tierName) = SkuMaxRawAndCapFor(r);   // MinMax/IdealMax/MaxMax picked by OTS-vs-avg band
                                var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                var remBefore = remaining;
                                var take = Math.Min(tierCap - current, remaining);
                                if (take <= 0)
                                {
                                    RecordTrace(2, i, r, tierName, tierCap, current, remBefore, 0, skipReason: "CapReached");
                                    continue;
                                }
                                allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 2);
                                remaining -= take;
                                RecordTrace(2, i, r, tierName, tierCap, current, remBefore, take);
                            }
                        }

                        // ---------- Pass 3: negative-OTS stores up to Min-Min (- SOH) ----------
                        // Over-stocked stores are treated conservatively — cap them at the
                        // MinMin safety floor rather than MinMax, so leftover units keep
                        // flowing to healthier stores or fall to Pass 4 for review.
                        if (remaining > 0)
                        {
                            var pass3Stores = SortByGroupThenOts(eligible.Where(r => LiveOtsPct(r) < 0)).ToList();
                            for (var i = 0; i < pass3Stores.Count; i++)
                            {
                                if (remaining <= 0) break;
                                var r = pass3Stores[i];
                                var cap = MinMinCapFor(r);
                                var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                var remBefore = remaining;
                                var take = Math.Min(cap - current, remaining);
                                if (take <= 0)
                                {
                                    RecordTrace(3, i, r, "MinMin", cap, current, remBefore, 0, skipReason: "CapReached");
                                    continue;
                                }
                                allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 3, tierNameOverride: "MinMin");
                                remaining -= take;
                                RecordTrace(3, i, r, "MinMin", cap, current, remBefore, take);
                            }
                        }

                        // ---------- Pass 4: <10% left -> proportional A/B/C by MinMax; else FLAG ----------
                        if (remaining > 0 && line.Qty > 0)
                        {
                            var pct = (double)remaining / line.Qty;
                            if (pct >= 0.10)
                            {
                                // Flag the item — persist to WmsPlanningFlag on LPMSIM.
                                await using (var wFlag = OpenOnPremBackup())
                                {
                                    await wFlag.ExecuteAsync(new CommandDefinition(@"
                                        INSERT dbo.WmsPlanningFlag
                                            (ContNo, PONo, ItemCode, DivCode, PoQty, RemainingQty, RunOption, FlaggedBy)
                                        VALUES (@c, @p, @i, @d, @q, @r, @o, @u)",
                                        new
                                        {
                                            c = line.ContNo, p = line.OraPONo, i = line.ItemCode,
                                            d = (int?)divCode, q = line.Qty, r = remaining,
                                            o = runOption.ToString(), u = user.Name ?? "",
                                        },
                                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                                }
                                // Also record a synthetic "Flagged" trace row so SUM(Take)
                                // across the trace for (ContNo, Itemcode) still reconciles
                                // to line.Qty. Direct append (not RecordTrace) because
                                // there is no real store: StoreID = "Flagged", Cap = Take
                                // = the dropped remainder, SkipReason = "Flagged".
                                if (trace is not null)
                                {
                                    trace.Add(new AllocationTraceRow(
                                        ContNo: line.ContNo, Itemcode: line.ItemCode, StoreID: "Flagged",
                                        DivCode: divCode, Pass: 4, SortRank: 0,
                                        VolumeGroup: null, TierName: "Flagged",
                                        LiveOtsPctBefore: null,
                                        Cap: remaining, Soh: 0,
                                        CurrentBeforeTake: 0,
                                        RemainingBefore: remaining,
                                        Take: remaining,
                                        RemainingAfter: 0,
                                        RunningOtsQtyAfter: 0,
                                        RunOption: runOption.ToString(),
                                        SkipReason: "Flagged",
                                        DefaultSkuMax: null, RawSkuMax: null, RatioSkuMax: null,
                                        AvgOtsPercent: avgOtsDecimal,
                                        AvgOtsMin: avgOtsMinDecimal,
                                        AvgOtsMax: avgOtsMaxDecimal,
                                        InitialOtsPct: null,
                                        PONo: line.OraPONo,
                                        LPMDt: line.LPMDt,
                                        Country: null));   // synthetic row — no store
                                }
                                remaining = 0;   // drop; move on to next item
                            }
                            else
                            {
                                // Distribute proportionally to A/B/C stores by raw MinMax.
                                // Filter: VG in {A, B, C} AND LiveOts > 0. Sort: LiveOts desc
                                // (across A/B/C flat — highest-OTS store first regardless of
                                // grade). Ratio uses raw MinMax as the weight; each store's
                                // share is take-as-is (no per-store cap).
                                var top3 = eligible
                                    .Where(r => (r.VolumeGroup?.Trim().ToUpperInvariant()) is "A" or "B" or "C")  // A, B, C by letter (decoupled from SortOrder config so an S=Special row between B and C can't shove C out of the top-3 rank)
                                    .Where(r => LiveOtsPct(r) > 0)                                                // positive-OTS stores only
                                    .Select(r => (Row: r, MinMax: RawMinMaxFor(r)))
                                    .Where(x => x.MinMax > 0)
                                    .OrderByDescending(x => LiveOtsPct(x.Row))
                                    .ToList();
                                var totalMinMax = top3.Sum(x => x.MinMax);
                                if (top3.Count > 0 && totalMinMax > 0)
                                {
                                    // FLOOR-based distribution (largest-remainder-lite):
                                    //   floorShare(i) = FLOOR(RawSkuMax(i) * origRemaining / totalMinMax)
                                    //   leftover      = origRemaining - SUM(floorShare)      (always >= 0)
                                    //   +1 handed out to the first `leftover` stores in sort order
                                    //   (i.e. highest LiveOts across A/B/C).
                                    // Guarantees SUM(Take) == origRemaining exactly — never negative
                                    // remaining. RatioSkuMax records the pure ROUND for audit only.
                                    var origRemaining = remaining;
                                    var floorShares = new int[top3.Count];
                                    var roundShares = new int[top3.Count];
                                    var shareSum = 0;
                                    for (int i = 0; i < top3.Count; i++)
                                    {
                                        var raw = (double)origRemaining * top3[i].MinMax / totalMinMax;
                                        floorShares[i] = (int)Math.Floor(raw);
                                        roundShares[i] = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
                                        shareSum += floorShares[i];
                                    }
                                    var takeShares = (int[])floorShares.Clone();
                                    var leftover = origRemaining - shareSum;   // >= 0 since floor <= raw
                                    for (int j = 0; j < leftover && j < takeShares.Length; j++)
                                        takeShares[j] += 1;

                                    for (int i = 0; i < top3.Count; i++)
                                    {
                                        var (r, minMax) = top3[i];
                                        var ratioShare  = roundShares[i];  // pure ROUND ratio (audit)
                                        var take        = takeShares[i];   // ratio + leftover on i==0
                                        // Pass 4 sizes off the ratio, not CapFor, so the
                                        // ECOM container budget has to be applied here too.
                                        if (IsEcomStore(r.StoreID))
                                            take = Math.Min(take, EcomRemainingAllowance(r.DivCode));
                                        var current = allocs.TryGetValue(r.StoreID, out var row) ? row.AllocQty : 0;
                                        var remBefore = remaining;
                                        if (take <= 0)
                                        {
                                            RecordTrace(4, i, r, "MinMax", minMax, current, remBefore, 0, skipReason: "ShareZero", ratioSkuMaxOverride: ratioShare);
                                            continue;
                                        }
                                        allocs[r.StoreID] = BumpRow(row, r, take, 0, pass: 4, tierNameOverride: "MinMax")
                                            with { RatioSkuMax = ratioShare };
                                        remaining -= take;
                                        RecordTrace(4, i, r, "MinMax", minMax, current, remBefore, take, ratioSkuMaxOverride: ratioShare);
                                    }
                                }
                            }
                        }

                        // Stamp MinMinCoverPct on every row for this item (same
                        // value across all its store rows). Only when Bypass Pass 1b
                        // was ticked; null otherwise so operators can spot bypass-
                        // affected items in the grid.
                        if (minMinCoverPct.HasValue)
                        {
                            foreach (var storeId in allocs.Keys.ToList())
                                allocs[storeId] = allocs[storeId] with { MinMinCoverPct = minMinCoverPct };
                        }
                    }

                    // Refresh runningOtsQty AFTER this item so the next item's
                    // Pass thresholds see the reduced OTS.
                    foreach (var kv in allocs)
                    {
                        var key = (kv.Key, divCode);
                        runningOtsQty[key] = runningOtsQty.GetValueOrDefault(key, 0) - kv.Value.AllocQty;
                    }
                }
                else
                {
                    while (remaining > 0)
                    {
                        bool any = false;
                        foreach (var s in stores)
                        {
                            if (remaining <= 0) break;
                            var current = allocs.TryGetValue(s.StoreID, out var row) ? row.AllocQty : 0;
                            if (current >= s.SKUMax) continue;
                            allocs[s.StoreID] = row is null
                                ? MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, 1, 0)
                                : row with { AllocQty = current + 1 };
                            remaining--;
                            any = true;
                        }
                        if (!any) break;
                    }
                }

                // FillSKUMax pass 2: round-robin extras when cap hit but qty remains.
                if (runOption == RunOption.FillSKUMax && remaining > 0 && stores.Count > 0)
                {
                    int idx = 0;
                    while (remaining > 0)
                    {
                        var s = stores[idx % stores.Count];
                        if (allocs.TryGetValue(s.StoreID, out var row))
                        {
                            allocs[s.StoreID] = row with
                            {
                                AllocQty        = row.AllocQty + 1,
                                RoundRobinExtra = row.RoundRobinExtra + 1,
                            };
                        }
                        else
                        {
                            allocs[s.StoreID] = MakeRow(s.StoreID, s.Country, s.VolumeGroup, s.MerchNeedMonth, s.SKUMax, 1, 1);
                        }
                        remaining--;
                        idx++;
                    }
                }

                // Commit allocations + mutate runningAlloc so next item's OTS reflects what
                // we just gave out.
                foreach (var row in allocs.Values)
                {
                    result.Add(row);
                    var key = (row.StoreID, divCode);
                    runningAlloc[key] = runningAlloc.GetValueOrDefault(key, 0) + row.AllocQty;
                }
            }
        }

        // Sanity check: total allocated must equal sum of PO line qtys (Fill SKUMax)
        // or be <= total in RoundRobin (excess unallocated when all stores at cap).
        var poTotal = lines.Sum(l => l.Qty);
        var allocTotal = result.Sum(r => r.AllocQty);
        if (runOption == RunOption.FillSKUMax && allocTotal != poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: Fill SKUMax allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");
        if (runOption == RunOption.RoundRobin && allocTotal > poTotal)
            Console.Error.WriteLine($"[ContainerAllocation] WARN: RoundRobin over-allocated {allocTotal} vs PO total {poTotal} (delta {allocTotal - poTotal}).");

        // Trace flush — writes one row per Pass touch to LPMSIM.dbo.WmsAllocationTrace
        // when the operator ticked "Trace Allocation". No-op otherwise. Delete any
        // existing rows for this ContNo first so re-processing gives a clean picture.
        if (trace is { Count: > 0 })
        {
            progress?.Report(new AllocationProgress(0, trace.Count, "Writing allocation trace"));
            await using var ct1 = OpenOnPremBackup();
            ct1.ChangeDatabase("LPMSIM");
            await ct1.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.WmsAllocationTrace WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var tdt = new DataTable();
            tdt.Columns.Add("ContNo",             typeof(string));
            tdt.Columns.Add("Itemcode",           typeof(string));
            tdt.Columns.Add("StoreID",            typeof(string));
            tdt.Columns.Add("DivCode",            typeof(int));
            tdt.Columns.Add("Pass",               typeof(byte));
            tdt.Columns.Add("SortRank",           typeof(int));
            tdt.Columns.Add("VolumeGroup",        typeof(string));
            tdt.Columns.Add("TierName",           typeof(string));
            tdt.Columns.Add("LiveOtsPctBefore",   typeof(decimal));
            tdt.Columns.Add("Cap",                typeof(int));
            tdt.Columns.Add("Soh",                typeof(int));
            tdt.Columns.Add("CurrentBeforeTake",  typeof(int));
            tdt.Columns.Add("RemainingBefore",    typeof(int));
            tdt.Columns.Add("Take",               typeof(int));
            tdt.Columns.Add("RemainingAfter",     typeof(int));
            tdt.Columns.Add("RunningOtsQtyAfter", typeof(int));
            tdt.Columns.Add("RunOption",          typeof(string));
            tdt.Columns.Add("RunBy",              typeof(string));
            tdt.Columns.Add("SkipReason",         typeof(string));
            tdt.Columns.Add("DefaultSkuMax",      typeof(int));
            tdt.Columns.Add("RawSkuMax",          typeof(int));
            tdt.Columns.Add("RatioSkuMax",        typeof(int));
            tdt.Columns.Add("AvgOtsPercent",      typeof(decimal));
            tdt.Columns.Add("AvgOtsMin",          typeof(decimal));
            tdt.Columns.Add("AvgOtsMax",          typeof(decimal));
            tdt.Columns.Add("InitialOtsPct",      typeof(decimal));
            tdt.Columns.Add("PONo",               typeof(string));
            tdt.Columns.Add("LPMDt",              typeof(DateTime));
            tdt.Columns.Add("Country",            typeof(string));

            foreach (var t in trace)
            {
                tdt.Rows.Add(
                    t.ContNo, t.Itemcode, t.StoreID, t.DivCode, (byte)t.Pass, t.SortRank,
                    (object?)t.VolumeGroup ?? DBNull.Value,
                    (object?)t.TierName    ?? DBNull.Value,
                    (object?)t.LiveOtsPctBefore ?? DBNull.Value,
                    t.Cap, t.Soh, t.CurrentBeforeTake, t.RemainingBefore, t.Take,
                    t.RemainingAfter, t.RunningOtsQtyAfter, t.RunOption,
                    (object?)user.Name ?? DBNull.Value,
                    (object?)t.SkipReason     ?? DBNull.Value,
                    (object?)t.DefaultSkuMax  ?? DBNull.Value,
                    (object?)t.RawSkuMax      ?? DBNull.Value,
                    (object?)t.RatioSkuMax    ?? DBNull.Value,
                    (object?)t.AvgOtsPercent  ?? DBNull.Value,
                    (object?)t.AvgOtsMin      ?? DBNull.Value,
                    (object?)t.AvgOtsMax      ?? DBNull.Value,
                    (object?)t.InitialOtsPct  ?? DBNull.Value,
                    (object?)t.PONo           ?? DBNull.Value,
                    (object?)t.LPMDt          ?? DBNull.Value,
                    (object?)t.Country        ?? DBNull.Value);
            }

            using var tbulk = new SqlBulkCopy(ct1)
            {
                DestinationTableName = "dbo.WmsAllocationTrace",
                BatchSize = 5000,
                BulkCopyTimeout = CommandTimeoutSeconds,
            };
            foreach (DataColumn col in tdt.Columns)
                tbulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await tbulk.WriteToServerAsync(tdt, ct);
        }

        // Bypass Pass 1b audit flush — one row per PO line item, DELETE-by-ContNo
        // then bulk-insert so re-runs show the latest calc only. Only fires when
        // the operator ticked Bypass Pass 1b on an FMMPO run.
        if (pass1BypassAudit is { Count: > 0 })
        {
            progress?.Report(new AllocationProgress(0, pass1BypassAudit.Count, "Writing Bypass Pass 1b audit"));
            await using var cb = OpenOnPremBackup();
            cb.ChangeDatabase("LPMSIM");
            await cb.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.Pass1ByPass WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var bdt = new DataTable();
            bdt.Columns.Add("ContNo",         typeof(string));
            bdt.Columns.Add("PONo",           typeof(string));
            bdt.Columns.Add("Itemcode",       typeof(string));
            bdt.Columns.Add("POQty",          typeof(int));
            bdt.Columns.Add("ABCMax",         typeof(int));
            bdt.Columns.Add("ABCSOH",         typeof(int));
            bdt.Columns.Add("ABCReqdStock",   typeof(int));
            bdt.Columns.Add("MinMinCoverPct", typeof(decimal));
            foreach (var a in pass1BypassAudit)
                bdt.Rows.Add(contno, a.PONo, a.Itemcode, a.PoQty, a.ABCMax, a.ABCSOH, a.ABCReqdStock, a.MinMinCoverPct);

            using var bbulk = new SqlBulkCopy(cb)
            {
                DestinationTableName = "dbo.Pass1ByPass",
                BatchSize = 5000,
                BulkCopyTimeout = CommandTimeoutSeconds,
            };
            foreach (DataColumn col in bdt.Columns)
                bbulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bbulk.WriteToServerAsync(bdt, ct);
        }

        return new AllocationProcessResult(result, blocked, trace);
    }

    // ===================== Save Draft (LPMSIM tables) =====================
    // Detail rows go via SqlBulkCopy — 7000+ row drafts went from
    // "per-row INSERT one at a time, minutes of wall-time" to a couple
    // of seconds. Header still uses a normal INSERT (one row).
    public async Task SaveDraftAsync(string country, string contno, IReadOnlyList<AllocationRow> rows,
        string? warehouse = null, RunOption runOption = RunOption.FillSKUMax,
        IProgress<AllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var totalQty = rows.Sum(r => r.AllocQty);
        await using var c = OpenOnPremBackup();

        // 1) Wipe any prior draft for this (Country, ContNo) so re-Save replaces cleanly.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: cleaning prior data"));
        await c.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c;
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c;",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 2) Header — single row.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: writing header"));
        await c.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO LPMSIM.dbo.WMS_ContAllocationDraftHeader
                (Country, ContNo, Warehouse, RunOption, RowCount1, TotalQty, SavedTS, SavedBy)
            VALUES (@ct, @c, @wh, @ro, @rc, @tq, DATEADD(hour, 4, SYSUTCDATETIME()), @u);",
            new { ct = country, c = contno, wh = warehouse, ro = runOption.ToString(),
                  rc = rows.Count, tq = totalQty, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 3) Detail — SqlBulkCopy. Build a DataTable that mirrors the 17 columns
        //    the previous per-row INSERT was populating; everything else stays NULL.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving draft: bulk insert"));
        var dt = new DataTable();
        dt.Columns.Add("Country",          typeof(string));
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("PoQty",            typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("PriorityRank",     typeof(int));
        dt.Columns.Add("MnwToday",         typeof(int));

        var now = DateTime.UtcNow.AddHours(4);  // GST stamp for Trndate/Time1
        var trnDate = now.Date;
        var time1 = new TimeSpan(now.Hour, now.Minute, now.Second);

        foreach (var r in rows)
        {
            dt.Rows.Add(
                country,
                r.Contno,
                trnDate,
                time1,
                r.ItemCode,
                r.ItemCode,
                r.VolumeGroup,
                r.AllocQty,
                r.PoQty,
                0,
                r.StoreID,
                r.Contno,
                (object?)r.ItemName ?? DBNull.Value,
                country,
                (object?)r.LPMDt ?? DBNull.Value,
                r.OraPONo,
                (object?)r.Division ?? DBNull.Value,
                (object?)(r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null) ?? DBNull.Value,
                (object?)r.PriorityRank ?? DBNull.Value,
                (object?)r.MnwToday ?? DBNull.Value);
        }

        // SqlBulkCopy.DestinationTableName needs the table to be in the connection's
        // current DB context; the existing OnPremBackup connection is on a different
        // database. Switch context to LPMSIM for the duration of the bulk copy.
        c.ChangeDatabase("LPMSIM");

        using var bulk = new SqlBulkCopy(c)
        {
            DestinationTableName = "dbo.WMS_ContAllocationDraftDetail",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
            NotifyAfter          = 500,
        };
        bulk.SqlRowsCopied += (_, e) =>
            progress?.Report(new AllocationProgress((int)e.RowsCopied, rows.Count, "Saving draft to LPMSIM"));
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        progress?.Report(new AllocationProgress(rows.Count, rows.Count, "Saving draft: done"));
    }

    public async Task<List<AllocationRow>> LoadDraftAsync(string country, string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Read draft detail; map back to AllocationRow shape. Several fields
        // (VolumeGroup, MerchNeedMonth, SkuMax, Brand, StoreName, DivCode) aren't
        // persisted on the detail row so they come back as defaults — preview
        // grid still works, sums/totals stay correct.
        var rows = (await c.QueryAsync<(string ContNo, string? OraPONo, string? ItemCode, string? ItemName,
                                       int? Qty, int? PoQty, string? StoreID, string? GroupCode, string? Division,
                                       string? Remarks, DateTime? LPMDt, int? PriorityRank, int? MnwToday)>(new CommandDefinition(@"
            SELECT ContNo, ORAPONo, Itemcode, Itemname, Qty, PoQty, StoreID, GroupCode, Division, Remarks, LPMDt, PriorityRank, MnwToday
            FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WITH (NOLOCK)
            WHERE Country = @ct AND ContNo = @c
            ORDER BY IdNo",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        return rows.Select(r => new AllocationRow(
            Contno: r.ContNo,
            OraPONo: r.OraPONo ?? "",
            ItemCode: r.ItemCode ?? "",
            ItemName: r.ItemName,
            Brand: null,
            PoQty: r.PoQty ?? 0,
            StoreID: r.StoreID ?? "",
            StoreName: null,
            Country: country,
            Division: r.Division,
            VolumeGroup: r.GroupCode ?? "",
            SkuMax: 0,
            AllocQty: r.Qty ?? 0,
            MerchNeedMonth: 0,
            DivCode: 0,
            RoundRobinExtra: ParseRoundRobin(r.Remarks),
            LPM: null,
            LPMDt: r.LPMDt,
            PriorityRank: r.PriorityRank,
            MnwToday: r.MnwToday
        )).ToList();
    }

    private static int ParseRoundRobin(string? remarks)
    {
        if (string.IsNullOrEmpty(remarks) || !remarks.StartsWith("RR+")) return 0;
        return int.TryParse(remarks.AsSpan(3), out var n) ? n : 0;
    }

    /// <summary>
    /// Bulk-write the allocation rows DIRECTLY to LPMSIM.dbo.WMS_ContAllocationData
    /// (no draft round-trip). Used by the simplified Container Allocation flow where
    /// Process always saves immediately.
    /// </summary>
    public async Task<int> SaveFinalDirectAsync(string genCountry, string contno, string allocationCountries,
        string? warehouse, IReadOnlyList<AllocationRow> rows, RunOption runOption,
        IReadOnlyList<BlockedItemRow>? blocked = null,
        IProgress<AllocationProgress>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();

        // 1) Find any prior Header batches for (GenCountry, ContNo, RunOption) — re-Process
        //    replaces the matching slice. Delete their detail + blocked + header rows.
        //    Sub-progress so the user can tell which DELETE is the slow one.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: looking up prior batches"));
        var priorBatches = (await c.QueryAsync<int>(new CommandDefinition(@"
            SELECT BatchNo FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
            WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro",
            new { gc = genCountry, c = contno, ro = roTag },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        if (priorBatches.Count > 0)
        {
            progress?.Report(new AllocationProgress(0, rows.Count, $"Saving: deleting prior detail rows ({priorBatches.Count} batch(es))"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationData    WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(0, rows.Count, "Saving: deleting prior blocked rows"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationBlocked WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(0, rows.Count, "Saving: deleting prior header rows"));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WHERE BatchNo IN @bs",
                new { bs = priorBatches }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        // 2) Create the new Header row and read back BatchNo.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: creating header row"));
        var totalQty = rows.Sum(r => r.AllocQty);
        var batchNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(@"
            INSERT INTO LPMSIM.dbo.WMS_Cont_Allocation_Header
                (ContNo, Warehouse, GenCountry, Country, RunOption,
                 RowCount1, TotalQty, ProcessedBy)
            VALUES (@c, @wh, @gc, @ac, @ro, @rc, @tq, @u);
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { c = contno, wh = warehouse, gc = genCountry, ac = allocationCountries,
                  ro = roTag, rc = rows.Count, tq = totalQty, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // 3) Write blocked rows via SqlBulkCopy. Large containers can produce
        // thousands of blocked rows (item eligibility blocks), and the previous
        // per-row Dapper Execute loop was ~10ms per round-trip — dominant cost
        // on containers with 4k+ blocks.
        if (blocked is { Count: > 0 })
        {
            progress?.Report(new AllocationProgress(0, rows.Count, $"Saving: bulk inserting {blocked.Count:N0} blocked row(s)"));
            var bdt = new System.Data.DataTable();
            bdt.Columns.Add("BatchNo",     typeof(int));
            bdt.Columns.Add("Country",     typeof(string));
            bdt.Columns.Add("ContNo",      typeof(string));
            bdt.Columns.Add("RunOption",   typeof(string));
            bdt.Columns.Add("ItemCode",    typeof(string));
            bdt.Columns.Add("ItemName",    typeof(string));
            bdt.Columns.Add("StoreID",     typeof(string));
            bdt.Columns.Add("StoreName",   typeof(string));
            bdt.Columns.Add("DivCode",     typeof(int));
            bdt.Columns.Add("Division",    typeof(string));
            bdt.Columns.Add("Department",  typeof(string));
            bdt.Columns.Add("PoQty",       typeof(int));
            bdt.Columns.Add("BlockReason", typeof(string));
            bdt.Columns.Add("CreatedBy",   typeof(string));
            foreach (var b in blocked)
            {
                bdt.Rows.Add(
                    batchNo,
                    b.Country, b.Contno, roTag,
                    b.ItemCode,
                    (object?)b.ItemName   ?? DBNull.Value,
                    b.StoreID,
                    (object?)b.StoreName  ?? DBNull.Value,
                    b.DivCode,
                    (object?)b.Division   ?? DBNull.Value,
                    (object?)b.Department ?? DBNull.Value,
                    b.PoQty,
                    (object?)b.BlockReason ?? DBNull.Value,
                    user.Name);
            }

            using var bulkBlk = new SqlBulkCopy(c)
            {
                DestinationTableName = "LPMSIM.dbo.WMS_ContAllocationBlocked",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in bdt.Columns)
                bulkBlk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulkBlk.WriteToServerAsync(bdt, ct);
        }

        // 4) Bulk-copy detail rows tagged with BatchNo + per-row Country + enrichment columns.
        // BuildingCategory now = Division (P3 spec; was the SIM country in P1/P2).
        // ResultType comes from WMS_Building_PalletTypes (S vs W per item season);
        // FinalResult mirrors ResultType (Q-A). Result stays NULL.
        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: bulk insert"));
        var dt = new System.Data.DataTable();
        dt.Columns.Add("BatchNo",          typeof(int));
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("Country",          typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("POQty",            typeof(int));
        dt.Columns.Add("SkuMax",           typeof(int));
        dt.Columns.Add("AllocatedQty",     typeof(int));
        dt.Columns.Add("PrevAllocatedQty", typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Brand",            typeof(string));
        dt.Columns.Add("DivCode",          typeof(int));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Size",             typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(decimal));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("OTS",              typeof(double));
        dt.Columns.Add("PriorityRank",     typeof(int));
        dt.Columns.Add("MnwToday",         typeof(int));
        dt.Columns.Add("Phase2Qty",        typeof(int));
        dt.Columns.Add("Pass1Qty",         typeof(int));
        dt.Columns.Add("Pass2Qty",         typeof(int));
        dt.Columns.Add("Pass3Qty",         typeof(int));
        dt.Columns.Add("Pass4Qty",         typeof(int));
        dt.Columns.Add("RatioSkuMax",      typeof(int));
        dt.Columns.Add("AvgOtsPercent",    typeof(decimal));
        dt.Columns.Add("SkuMaxBand",       typeof(string));
        dt.Columns.Add("AvgOtsMin",        typeof(decimal));
        dt.Columns.Add("AvgOtsMax",        typeof(decimal));
        dt.Columns.Add("InitialOtsPct",    typeof(decimal));
        dt.Columns.Add("Soh",              typeof(int));
        dt.Columns.Add("RunningOtsQty",    typeof(int));
        dt.Columns.Add("OtsQtyToday",      typeof(int));
        dt.Columns.Add("TgtEOM",           typeof(int));
        dt.Columns.Add("RawSkuMax",        typeof(int));
        dt.Columns.Add("MinMinCoverPct",   typeof(decimal));

        var now = DateTime.UtcNow.AddHours(4);  // GST stamp for Trndate/Time1
        var trnDate = now.Date;
        var time1 = new TimeSpan(now.Hour, now.Minute, now.Second);

        foreach (var r in rows)
        {
            dt.Rows.Add(
                batchNo,
                r.Contno, r.Country, trnDate, time1, r.ItemCode, r.ItemCode,
                r.ItemCode,                                  // Barcode = ItemCode
                r.VolumeGroup,
                r.PoQty, r.SkuMax, r.AllocQty, r.PrevAllocatedQty, 0,  // POQty = PO qty, AllocatedQty = alloc qty
                r.StoreID, r.Contno,
                (object?)r.ItemName ?? DBNull.Value,
                (object?)r.Division ?? DBNull.Value,         // BuildingCategory = Division
                (object?)r.LPMDt ?? DBNull.Value, r.OraPONo,
                (object?)r.Division ?? DBNull.Value,
                (object?)r.Brand ?? DBNull.Value,
                r.DivCode,
                (object?)r.Department ?? DBNull.Value,
                (object?)r.Season ?? DBNull.Value,
                (object?)r.Style ?? DBNull.Value,
                (object?)r.Size ?? DBNull.Value,
                (object?)r.SalesPrice ?? DBNull.Value,
                (object?)r.PalletType ?? DBNull.Value,        // ResultType
                (object?)r.PalletType ?? DBNull.Value,        // FinalResult mirrors ResultType
                (object?)(r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null) ?? DBNull.Value,
                (object?)r.OTS ?? DBNull.Value,
                (object?)r.PriorityRank ?? DBNull.Value,
                (object?)r.MnwToday ?? DBNull.Value,
                (object?)r.Phase2Qty ?? DBNull.Value,
                (object?)r.Pass1Qty ?? DBNull.Value,
                (object?)r.Pass2Qty ?? DBNull.Value,
                (object?)r.Pass3Qty ?? DBNull.Value,
                (object?)r.Pass4Qty ?? DBNull.Value,
                (object?)r.RatioSkuMax ?? DBNull.Value,
                (object?)r.AvgOtsPercent ?? DBNull.Value,
                (object?)r.SkuMaxBand ?? DBNull.Value,
                (object?)r.AvgOtsMin ?? DBNull.Value,
                (object?)r.AvgOtsMax ?? DBNull.Value,
                (object?)r.InitialOtsPct ?? DBNull.Value,
                (object?)r.Soh ?? DBNull.Value,
                (object?)r.RunningOtsQty ?? DBNull.Value,
                (object?)r.OtsQtyToday ?? DBNull.Value,
                (object?)r.TgtEOM ?? DBNull.Value,
                (object?)r.RawSkuMax ?? DBNull.Value,
                (object?)r.MinMinCoverPct ?? DBNull.Value);
        }

        c.ChangeDatabase("LPMSIM");
        using var bulk = new SqlBulkCopy(c)
        {
            DestinationTableName = "dbo.WMS_ContAllocationData",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
            NotifyAfter          = 500,
        };
        bulk.SqlRowsCopied += (_, e) =>
            progress?.Report(new AllocationProgress((int)e.RowsCopied, rows.Count, "Saving to LPMSIM"));
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        progress?.Report(new AllocationProgress(rows.Count, rows.Count, "Saving: done"));
        return batchNo;
    }

    /// <summary>
    /// Sum of orgqty in usa.dbo.usaorgfile_LPM for the container — drives the
    /// 'Total PO Qty' card on the allocation page. Returns 0 when no rows match.
    /// </summary>
    public async Task<long> GetTotalPoQtyAsync(string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT CAST(ISNULL(SUM(orgqty),0) AS BIGINT) FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, commandTimeout: 60, cancellationToken: ct)) ?? 0;
    }

    /// <summary>
    /// Load rows from LPMSIM.dbo.WMS_ContAllocationData and map back to AllocationRow.
    /// Fields not stored in the final table (PoQty, SkuMax, VolumeGroup, etc.) come
    /// back as defaults; UI still displays Allocated Qty, StoreID, Division, etc.
    /// </summary>
    public async Task<List<AllocationRow>> LoadFinalAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        return await LoadAllocationDetailAsync(c,
            "JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo " +
            "WHERE h.GenCountry = @gc AND h.ContNo = @c AND h.RunOption = @ro",
            new { gc = genCountry, c = contno, ro = roTag }, ct);
    }

    /// <summary>
    /// Reset Final: deletes all rows from LPMSIM.dbo.WMS_ContAllocationData for the
    /// given container so the page unlocks and Process can run again. Destructive —
    /// caller is responsible for confirming with the user. Returns rows deleted.
    ///
    /// Refuses if any of the downstream Azure WMS state exists for this ContNo:
    ///   - dbo.WMS_ContAllocationData rows (allocation was already synced)
    ///   - dbo.WmsOpenBox rows            (building is open)
    ///   - dbo.WMSContBuildScanData rows  (someone already scanned pieces)
    /// Deleting the SIM-side allocation while any of these exist would leave
    /// the building side pointing at a phantom allocation.
    /// </summary>
    public async Task<int> ResetFinalAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();

        // Pre-check Azure downstream state. Refuse if anything exists.
        await using (var w = OpenWms())
        {
            var status = await w.QueryFirstAsync<(int AllocSynced, int OpenBoxes, int Scanned)>(new CommandDefinition(@"
                SELECT
                    (SELECT COUNT(*) FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c) AS AllocSynced,
                    (SELECT COUNT(*) FROM dbo.WmsOpenBox             WITH (NOLOCK) WHERE Contno = @c) AS OpenBoxes,
                    (SELECT COUNT(*) FROM dbo.WMSContBuildScanData   WITH (NOLOCK) WHERE ContNo = @c) AS Scanned",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var blockers = new List<string>();
            if (status.AllocSynced > 0) blockers.Add($"{status.AllocSynced} row(s) in dbo.WMS_ContAllocationData (already synced to Azure)");
            if (status.OpenBoxes   > 0) blockers.Add($"{status.OpenBoxes} row(s) in dbo.WmsOpenBox (building is open)");
            if (status.Scanned     > 0) blockers.Add($"{status.Scanned} row(s) in dbo.WMSContBuildScanData (building has started)");
            if (blockers.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot delete allocation for {contno} — {string.Join("; ", blockers)}. Clear the Azure side first.");
        }

        await using var c = OpenOnPremBackup();

        // One process per container: on Delete, clean out ALL run options for
        // the container (headers + details + blocked) AND the draft tables.
        // The runOption parameter is kept for the caller's messaging but no
        // longer filters the delete — otherwise a stale header for another
        // run option would still block the next Process click.
        var batches = (await c.QueryAsync<int>(new CommandDefinition(@"
            SELECT BatchNo FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
             WHERE GenCountry = @gc AND ContNo = @c",
            new { gc = genCountry, c = contno },
            commandTimeout: 120, cancellationToken: ct))).ToList();

        int detailDeleted = 0;
        if (batches.Count > 0)
        {
            detailDeleted = await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationData    WHERE BatchNo IN @bs",
                new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_ContAllocationBlocked WHERE BatchNo IN @bs",
                new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WHERE BatchNo IN @bs",
                new { bs = batches }, commandTimeout: 120, cancellationToken: ct));
        }

        // Also clear any lingering draft state for this container so the next
        // Process click starts from a clean slate. Drafts key on (Country, ContNo),
        // not BatchNo. (No draft-blocked table — blocked items are recorded only
        // at Process time against WMS_ContAllocationBlocked, cleared above.)
        await c.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail  WHERE Country = @ct AND ContNo = @c;
            DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader  WHERE Country = @ct AND ContNo = @c;",
            new { ct = genCountry, c = contno }, commandTimeout: 120, cancellationToken: ct));

        // Planning flags key on ContNo (not GenCountry/BatchNo) — clear them
        // too so a re-Process starts from a clean flag set.
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WmsPlanningFlag WHERE ContNo = @c",
            new { c = contno }, commandTimeout: 120, cancellationToken: ct));

        _ = roTag; // silence unused-warning while keeping the runOption param on the signature
        return detailDeleted;
    }

    /// <summary>Load saved blocked items for the (Container, RunOption).</summary>
    public async Task<List<BlockedItemRow>> LoadBlockedAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BlockedItemRow>(new CommandDefinition(@"
            SELECT b.ContNo AS Contno, b.ItemCode, b.ItemName, b.Division, b.Department,
                   b.StoreID, b.StoreName, b.Country, b.PoQty, b.DivCode, b.BlockReason
              FROM LPMSIM.dbo.WMS_ContAllocationBlocked b WITH (NOLOCK)
              JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = b.BatchNo
             WHERE h.GenCountry = @gc AND h.ContNo = @c AND h.RunOption = @ro
             ORDER BY b.ItemCode, b.StoreID",
            new { gc = genCountry, c = contno, ro = roTag },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AllocationStatus> GetStatusAsync(string genCountry, string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var d = await c.QueryFirstOrDefaultAsync<(int? RowCount1, int? TotalQty, string? RunOption)>(new CommandDefinition(
            "SELECT RowCount1, TotalQty, RunOption FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c",
            new { ct = genCountry, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var hasDraft = d.RowCount1 is not null;
        var draftRows = d.RowCount1 ?? 0;

        // Per-RunOption final row counts from the Header table. Each Process run creates
        // one Header row per (GenCountry, ContNo, RunOption); RowCount1 holds the saved total.
        var f = await c.QueryFirstOrDefaultAsync<(int Total, DateTime? Max1, int Fsm, int Rr, int Frr, int Fmm)>(new CommandDefinition(@"
            SELECT
                Total = ISNULL(SUM(RowCount1), 0),
                Max1  = MAX(ProcessedTS),
                Fsm   = ISNULL(SUM(CASE WHEN RunOption = 'FillSKUMax'           THEN RowCount1 ELSE 0 END), 0),
                Rr    = ISNULL(SUM(CASE WHEN RunOption = 'RoundRobin'           THEN RowCount1 ELSE 0 END), 0),
                Frr   = ISNULL(SUM(CASE WHEN RunOption = 'FillSKUMaxRoundRobin' THEN RowCount1 ELSE 0 END), 0),
                Fmm   = ISNULL(SUM(CASE WHEN RunOption = 'FillMinMinPlusOthers' THEN RowCount1 ELSE 0 END), 0)
            FROM LPMSIM.dbo.WMS_Cont_Allocation_Header
            WHERE GenCountry = @gc AND ContNo = @c",
            new { gc = genCountry, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var hasFinal = f.Total > 0;

        // Azure sync check: any row in dbo.WMS_ContAllocationData means the
        // container's allocation has been shipped to Azure. Once that happens
        // Delete must be blocked at the UI (Building may already be reading
        // those rows). Uses TOP 1 (index-friendly on IX_AzureCAD_ContItemPo)
        // + explicit command timeout so a growing mirror never hangs the
        // Status refresh, which is called from Process on every click.
        int azureRows = 0;
        await using (var w = OpenWms())
        {
            var exists = await w.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            azureRows = exists == 1 ? 1 : 0;
        }

        // Planning flags: count Pass 4 items dropped this container (>=10% residual)
        // and sum the RemainingQty so the UI can show "N items · Q qty" on the button.
        var pf = await c.QueryFirstAsync<(int Cnt, int TotalQty)>(new CommandDefinition(@"
            SELECT COUNT(*) AS Cnt, ISNULL(SUM(RemainingQty), 0) AS TotalQty
              FROM LPMSIM.dbo.WmsPlanningFlag WITH (NOLOCK)
             WHERE ContNo = @c",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new AllocationStatus(hasDraft, hasFinal, draftRows, f.Total, f.Max1, d.RunOption,
                                     f.Fsm, f.Rr, f.Frr, azureRows, f.Fmm, pf.Cnt, pf.TotalQty);
    }

    /// <summary>Load Planning Flag rows for a container — the FMMPO Pass 4
    /// items whose residual was ≥10% of PO qty and got dropped for planner review.</summary>
    public async Task<List<PlanningFlagRow>> LoadPlanningFlagsAsync(string contno, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<PlanningFlagRow>(new CommandDefinition(@"
            SELECT FlaggedTS, ContNo, PONo, ItemCode, DivCode, PoQty, RemainingQty, RunOption, FlaggedBy
              FROM LPMSIM.dbo.WmsPlanningFlag WITH (NOLOCK)
             WHERE ContNo = @c
             ORDER BY FlaggedTS DESC, ItemCode",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Confirm & Save (Draft -> WMS_ContAllocationData) =====================
    // Atomic-ish: INSERT...SELECT from draft into final, then DELETE drafts.
    // If a draft exists, prefers that (single SQL copy). Falls back to inserting
    // the in-memory rows if no draft exists yet.
    public async Task<int> SaveAllocationAsync(IReadOnlyList<AllocationRow> rows,
        IProgress<AllocationProgress>? progress = null, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var country = rows[0].Country;
        var contno  = rows[0].Contno;

        progress?.Report(new AllocationProgress(0, rows.Count, "Confirming: checking draft"));
        await using var c = OpenOnPremBackup();

        // Is there a saved draft for this (Country, ContNo)? If yes, copy and delete.
        var draftRows = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT COUNT(*) FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c",
            new { ct = country, c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) ?? 0;

        if (draftRows > 0)
        {
            progress?.Report(new AllocationProgress(0, draftRows, $"Confirming: copying {draftRows} rows draft → final"));
            var copySql = @"
                INSERT INTO LPMSIM.dbo.WMS_ContAllocationData
                  (ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Season, Department, Division,
                   Result, FinalResult, ResultType, Qty, QtyIssue, OrPrice, PrintFlag, RfidFlag,
                   Company, StoreID, Itemname, Barcode, SalesPrice, RefNo, Mark, Uid,
                   RStatus, RDateTime, PStatus, PDateTime, Excess, TcmContno, BuildingCategory,
                   LPMDt, LPMBoxNO, ORAPONo, Style, Remarks, PriorityRank, MnwToday)
                SELECT
                   ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Season, Department, Division,
                   Result, FinalResult, ResultType, Qty, QtyIssue, OrPrice, PrintFlag, RfidFlag,
                   Company, StoreID, Itemname, Barcode, SalesPrice, RefNo, Mark, Uid,
                   RStatus, RDateTime, PStatus, PDateTime, Excess, TcmContno, BuildingCategory,
                   LPMDt, LPMBoxNO, ORAPONo, Style, Remarks, PriorityRank, MnwToday
                FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail
                WHERE Country = @ct AND ContNo = @c;

                DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftDetail WHERE Country = @ct AND ContNo = @c;
                DELETE FROM LPMSIM.dbo.WMS_ContAllocationDraftHeader WHERE Country = @ct AND ContNo = @c;";
            await c.ExecuteAsync(new CommandDefinition(copySql, new { ct = country, c = contno },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            progress?.Report(new AllocationProgress(draftRows, draftRows, "Confirming: done"));
            return draftRows;
        }

        // Fallback path — no draft, insert in-memory rows directly.
        var insertSql = @"INSERT INTO LPMSIM.dbo.WMS_ContAllocationData
            (ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Division, Qty, QtyIssue,
             StoreID, TcmContno, ORAPONo, LPMDt, Itemname, BuildingCategory, Remarks, PriorityRank, MnwToday)
          VALUES
            (@ContNo, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE), CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS TIME(0)),
             @UPC, @ItemCode, @GroupCode, @Division, @Qty, 0,
             @StoreID, @ContNo, @OraPONo, @LPMDt, @ItemName, @Country, @Remarks, @PriorityRank, @MnwToday);";
        var affected = 0;
        foreach (var r in rows)
        {
            affected += await c.ExecuteAsync(new CommandDefinition(insertSql, new
            {
                ContNo    = r.Contno,  UPC = r.ItemCode, ItemCode = r.ItemCode,
                GroupCode = r.VolumeGroup, Division = r.Division, Qty = r.AllocQty,
                StoreID   = r.StoreID, OraPONo = r.OraPONo, LPMDt = r.LPMDt,
                ItemName  = r.ItemName, Country = r.Country,
                Remarks   = r.RoundRobinExtra > 0 ? $"RR+{r.RoundRobinExtra}" : null,
                PriorityRank = (int?)r.PriorityRank,
                MnwToday  = (int?)r.MnwToday,
            }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        return affected;
    }

    // ===================== Country / WH dropdowns (Azure WMS WHMaster) =====================
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT Country FROM dbo.WmsWHMaster WHERE Active = 1 ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<List<string>> GetWarehousesAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return new();
        await using var c = OpenWms();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT Warehouse FROM dbo.WmsWHMaster
              WHERE Active = 1 AND Country = @c ORDER BY Warehouse",
            new { c = country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    // ===================== P2: SIM countries (allocation destinations) =====================
    // Excludes 'Ex2Locations' — not a real allocation destination, per user request.
    public async Task<List<string>> GetSimCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT SIMCountry
                FROM bfldata.dbo.DataSettings WITH (NOLOCK)
               WHERE SIMCountry IS NOT NULL
                 AND LTRIM(RTRIM(SIMCountry)) <> ''
                 AND SIMCountry <> 'Ex2Locations'
               ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    // ===================== P2: Per-container allocation countries =====================
    /// <summary>
    /// Reads hodata.dbo.vUSAOrder.AllocationCountry for a container and narrows the
    /// SIM country list to what that order actually allows.
    ///
    /// vUSAOrder is keyed by refno = ContNo (NOT its own Contno column — see the
    /// note in CountingCompletionTodayService). A container can have several order
    /// rows, so every distinct AllocationCountry is unioned together.
    ///
    /// 'ALL', blank, or no matching order row all mean "no restriction" and return
    /// the full <paramref name="simCountries"/> list unchanged. Anything else is
    /// treated as a delimited list of country names and intersected with the SIM
    /// list, so a value naming a country that isn't a real allocation destination
    /// simply drops out rather than producing an unusable option.
    /// </summary>
    /// <returns>
    /// (Allowed countries, Restricted) — Restricted is false when the order imposes
    /// no filter, so callers can tell "everything, by default" from "everything,
    /// because the order happens to list them all".
    /// </returns>
    public async Task<(List<string> Allowed, bool Restricted)> GetAllocationCountriesForContainerAsync(
        string contno, IEnumerable<string> simCountries, CancellationToken ct = default)
    {
        var all = simCountries.ToList();
        if (string.IsNullOrWhiteSpace(contno)) return (all, false);

        await using var c = OpenOnPremBackup();
        var raw = await c.QueryAsync<string?>(new CommandDefinition(
            @"SELECT DISTINCT AllocationCountry
                FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
               WHERE refno = @c",
            new { c = contno.Trim() }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var values = raw.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (values.Count == 0) return (all, false);

        // Any row saying ALL lifts the restriction for the whole container.
        if (values.Any(v => string.Equals(v!.Trim(), "ALL", StringComparison.OrdinalIgnoreCase)))
            return (all, false);

        var named = values
            .SelectMany(v => v!.Split(new[] { ',', ';', '|', '/' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (named.Count == 0) return (all, false);

        var allowed = all.Where(sc => named.Contains(sc)).ToList();

        // Every named country fell outside the SIM list — treat as unrestricted
        // rather than handing back an empty dropdown nobody can proceed from.
        if (allowed.Count == 0) return (all, false);

        return (allowed, true);
    }

    // ===================== P2: Processed Contnos dropdown =====================
    public async Task<List<string>> GetProcessedContnosAsync(string genCountry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genCountry)) return new();
        await using var c = OpenOnPremBackup();
        // Last two days only, newest first. The list was every container ever
        // processed for the country and grew without bound; in practice the
        // operator is only ever picking something they ran today or yesterday.
        //
        // "Two days" = today and yesterday by GST DATE, not a rolling 48 hours —
        // a batch run at 09:00 yesterday should still be there at 16:00 today.
        var list = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT ContNo
                FROM (
                    SELECT ContNo, LastRun = MAX(ProcessedTS)
                      FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
                     WHERE GenCountry = @gc
                       AND ProcessedTS >= DATEADD(day, -1,
                             CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS date))
                     GROUP BY ContNo
                ) x
               ORDER BY LastRun DESC, ContNo",
            new { gc = genCountry }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return list.AsList();
    }

    /// <summary>
    /// Every WmsAllocationTrace row for a container, for the Trace Data Export button.
    ///
    /// Returned as (column names, rows-of-values) rather than a typed record on
    /// purpose: the trace table gains columns fairly often (PONo/LPMDt/Country were
    /// the last three), and a DTO would need editing in lockstep or the export
    /// would quietly omit them. SELECT * plus the reader's own schema means a new
    /// column appears in the spreadsheet the moment it exists in the table.
    /// </summary>
    public async Task<(List<string> Columns, List<object?[]> Rows)> GetAllocationTraceAsync(
        string contno, CancellationToken ct = default)
    {
        var cols = new List<string>();
        var rows = new List<object?[]>();
        if (string.IsNullOrWhiteSpace(contno)) return (cols, rows);

        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT * FROM LPMSIM.dbo.WmsAllocationTrace WITH (NOLOCK)
                             WHERE ContNo = @c
                             ORDER BY Itemcode, Pass, SortRank";
        cmd.Parameters.AddWithValue("@c", contno.Trim());
        cmd.CommandTimeout = CommandTimeoutSeconds;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        for (var i = 0; i < rdr.FieldCount; i++) cols.Add(rdr.GetName(i));
        while (await rdr.ReadAsync(ct))
        {
            var vals = new object?[rdr.FieldCount];
            for (var i = 0; i < rdr.FieldCount; i++)
                vals[i] = await rdr.IsDBNullAsync(i, ct) ? null : rdr.GetValue(i);
            rows.Add(vals);
        }
        return (cols, rows);
    }

    /// <summary>Latest batch (highest BatchNo) for (GenCountry, ContNo). When runOption is
    /// passed, scopes to that algorithm so the Process / Load flows can pick the right
    /// Header (a container can have one batch per RunOption).</summary>
    public async Task<BatchInfo?> GetLatestBatchInfoAsync(string genCountry, string contno,
        string? runOption = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genCountry) || string.IsNullOrWhiteSpace(contno)) return null;
        await using var c = OpenOnPremBackup();
        var b = await c.QueryFirstOrDefaultAsync<BatchInfo>(new CommandDefinition(@"
            SELECT TOP 1
                   BatchNo, ContNo, Warehouse, GenCountry, Country, RunOption,
                   RowCount1, TotalQty, ProcessedTS, ProcessedBy, ApprovedDt, ApprovedBy
              FROM LPMSIM.dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
             WHERE GenCountry = @gc AND ContNo = @c
               AND (@ro IS NULL OR RunOption = @ro)
             ORDER BY BatchNo DESC",
            new { gc = genCountry, c = contno, ro = runOption },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return b;
    }

    /// <summary>P4 Approve. Stamps ApprovedDt = SYSDATETIME() + ApprovedBy = current user
    /// on the latest Header matching (GenCountry, ContNo, RunOption). Returns true when a
    /// row was actually updated; false when no matching unapproved batch exists.</summary>
    public async Task<bool> ApproveAsync(string genCountry, string contno, RunOption runOption, CancellationToken ct = default)
    {
        var roTag = runOption.ToString();
        await using var c = OpenOnPremBackup();
        var n = await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE LPMSIM.dbo.WMS_Cont_Allocation_Header
               SET ApprovedDt = DATEADD(hour, 4, SYSUTCDATETIME()),
                   ApprovedBy = @u
             WHERE GenCountry = @gc AND ContNo = @c AND RunOption = @ro
               AND ApprovedDt IS NULL",
            new { gc = genCountry, c = contno, ro = roTag, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return n > 0;
    }

    /// <summary>Load detail rows for a specific BatchNo. Used by the "Load Processed Data" path.
    /// Delegates to the shared loader so the re-opened grid carries PoQty, LPM, Brand,
    /// StoreName, MerchNeedMonth, DivCode, OTS — the columns the report views need.</summary>
    public async Task<List<AllocationRow>> LoadFinalByBatchAsync(int batchNo, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await LoadAllocationDetailAsync(c, "WHERE d.BatchNo = @b", new { b = batchNo }, ct);
    }

    public async Task<List<BlockedItemRow>> LoadBlockedByBatchAsync(int batchNo, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BlockedItemRow>(new CommandDefinition(@"
            SELECT ContNo AS Contno, ItemCode, ItemName, Division, Department,
                   StoreID, StoreName, Country, PoQty, DivCode, BlockReason
              FROM LPMSIM.dbo.WMS_ContAllocationBlocked WITH (NOLOCK)
             WHERE BatchNo = @b
             ORDER BY ItemCode, StoreID",
            new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Shared loader for `WMS_ContAllocationData` rows. Caller supplies the JOIN +
    /// WHERE clause (e.g. " WHERE d.BatchNo = @b " or with a JOIN to Header by RunOption)
    /// and matching parameter object. Persisted columns (Itemname, GroupCode, OTS, ...)
    /// come straight from the detail row; transient fields (PoQty, Brand, StoreName,
    /// MerchNeedMonth, LPM, DivCode) are filled by 5 prefetches and joined in memory so
    /// every report view shows complete data when a batch is re-opened.
    /// </summary>
    private async Task<List<AllocationRow>> LoadAllocationDetailAsync(
        SqlConnection c, string joinAndWhereSql, object filterParams, CancellationToken ct)
    {
        var rows = (await c.QueryAsync<(string ContNo, string? OraPONo, string? ItemCode, string? ItemName, string? Brand,
                                       int? POQty, int? AllocatedQty, int? Phase2Qty, int? SkuMax, int? DivCode, string? StoreID, string? Country, string? GroupCode, string? Division,
                                       string? Remarks, DateTime? LPMDt, double? OTS, int? PriorityRank, int? MnwToday,
                                       int? Pass1Qty, int? Pass2Qty, int? Pass3Qty, int? Pass4Qty, decimal? AvgOtsPercent,
                                       int? OtsQtyToday, int? TgtEOM, int? RawSkuMax, int? RatioSkuMax, decimal? MinMinCoverPct)>(new CommandDefinition($@"
            SELECT d.ContNo, d.ORAPONo, d.Itemcode, d.Itemname, d.Brand, d.POQty, d.AllocatedQty, d.Phase2Qty, d.SkuMax, d.DivCode, d.StoreID, d.Country,
                   d.GroupCode, d.Division, d.Remarks, d.LPMDt, d.OTS, d.PriorityRank, d.MnwToday,
                   d.Pass1Qty, d.Pass2Qty, d.Pass3Qty, d.Pass4Qty, d.AvgOtsPercent, d.OtsQtyToday, d.TgtEOM, d.RawSkuMax, d.RatioSkuMax, d.MinMinCoverPct
              FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
              {joinAndWhereSql}
             ORDER BY d.IdNo",
            filterParams, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (rows.Count == 0) return new();

        var distinctContnos = rows.Select(r => r.ContNo).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        var distinctItems   = rows.Select(r => r.ItemCode!).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        var distinctStores  = rows.Select(r => r.StoreID!).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();

        // PoQty + LPM per (ContNo, OraPONo, ItemCode) from usaorgfile_LPM.
        var poInfo = new Dictionary<(string ContNo, string OraPONo, string ItemCode), (int Qty, string? LPM)>();
        if (distinctContnos.Length > 0)
        {
            var poRows = await c.QueryAsync<(string ContNo, string? OraPONo, string ItemCode, int? Qty, string? LPM)>(new CommandDefinition(@"
                SELECT ContNo, OraPONo, ItemCode,
                       SUM(CAST(ISNULL(orgqty,0) AS INT)) AS Qty,
                       MAX(LPM)                          AS LPM
                  FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
                 WHERE ContNo IN @contnos
                 GROUP BY ContNo, OraPONo, ItemCode",
                new { contnos = distinctContnos }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var p in poRows) poInfo[(p.ContNo, p.OraPONo ?? "", p.ItemCode)] = (p.Qty ?? 0, p.LPM);
        }

        // Brand: read directly from d.Brand (persisted on the detail row from this deploy
        // onwards). Legacy batches show NULL until re-processed.

        // StoreName per StoreID from DataSettings.
        var storeNameById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (distinctStores.Length > 0)
        {
            var snRows = await c.QueryAsync<(string StoreID, string? PBFullname)>(new CommandDefinition(@"
                SELECT StoreID, MAX(PBFullname) AS PBFullname
                  FROM bfldata.dbo.DataSettings WITH (NOLOCK)
                 WHERE StoreID IN @stores AND PBFullname IS NOT NULL
                 GROUP BY StoreID",
                new { stores = distinctStores }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var s in snRows) storeNameById[s.StoreID] = s.PBFullname;
        }

        // DivCode: read directly from d.DivCode (persisted from this deploy onwards).

        // MerchNeedMonth per (StoreID, DivCode) for the current month. Use the per-row
        // d.DivCode values from the rows just loaded to build the @divs filter.
        var merchByKey = new Dictionary<(string StoreID, int DivCode), int>();
        var distinctDivs = rows.Where(r => r.DivCode is > 0).Select(r => r.DivCode!.Value).Distinct().ToArray();
        if (distinctStores.Length > 0 && distinctDivs.Length > 0)
        {
            var merchRows = await c.QueryAsync<(string StoreID, int DivCode, int MerchNeedMonth)>(new CommandDefinition(@"
                SELECT StoreID, DivCode, ISNULL(MerchNeedMonth, 0) AS MerchNeedMonth
                  FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE StoreID IN @stores AND DivCode IN @divs
                   AND Month1 = MONTH(DATEADD(hour, 4, SYSUTCDATETIME())) AND Year1 = YEAR(DATEADD(hour, 4, SYSUTCDATETIME()))",
                new { stores = distinctStores, divs = distinctDivs }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var m in merchRows) merchByKey[(m.StoreID, m.DivCode)] = m.MerchNeedMonth;
        }

        return rows.Select(r =>
        {
            var item    = r.ItemCode ?? "";
            var store   = r.StoreID ?? "";
            var divCode = r.DivCode ?? 0;
            poInfo.TryGetValue((r.ContNo, r.OraPONo ?? "", item), out var po);
            storeNameById.TryGetValue(store, out var storeName);
            merchByKey.TryGetValue((store, divCode), out var merch);

            return new AllocationRow(
                Contno: r.ContNo,
                OraPONo: r.OraPONo ?? "",
                ItemCode: item,
                ItemName: r.ItemName,
                Brand: r.Brand,
                PoQty: po.Qty,                                // always from usaorgfile_LPM join (authoritative)
                StoreID: store,
                StoreName: storeName,
                Country: r.Country ?? "",
                Division: r.Division,
                VolumeGroup: r.GroupCode ?? "",
                SkuMax: r.SkuMax ?? 0,
                AllocQty: r.AllocatedQty ?? r.POQty ?? 0,     // AllocatedQty is authoritative; fall back to POQty for legacy rows saved before AllocatedQty was populated
                MerchNeedMonth: merch,
                DivCode: divCode,
                RoundRobinExtra: ParseRoundRobin(r.Remarks),
                LPM: po.LPM,
                LPMDt: r.LPMDt,
                OTS: r.OTS,
                PriorityRank: r.PriorityRank,
                MnwToday: r.MnwToday,
                Phase2Qty: r.Phase2Qty,
                Pass1Qty: r.Pass1Qty,
                Pass2Qty: r.Pass2Qty,
                Pass3Qty: r.Pass3Qty,
                Pass4Qty: r.Pass4Qty,
                AvgOtsPercent: r.AvgOtsPercent,
                OtsQtyToday: r.OtsQtyToday,
                TgtEOM: r.TgtEOM,
                RawSkuMax: r.RawSkuMax,
                RatioSkuMax: r.RatioSkuMax,
                MinMinCoverPct: r.MinMinCoverPct);
        }).ToList();
    }

    /// <summary>Slim projection of dbo.WmsOtsPoAllocationRun used by the
    /// FillSKUMax+RoundRobin OTS-run-based algorithm.</summary>
    private sealed class OtsRunLookupRow
    {
        public string   Country          { get; set; } = "";
        public string   StoreID          { get; set; } = "";
        public int      DivCode          { get; set; }
        public string?  VolumeGroup      { get; set; }
        public int      TgtEOM           { get; set; }
        public int      SOHToday         { get; set; }
        public int      WeekSales        { get; set; }
        public int      InTransit        { get; set; }
        public int      Ex2DcSoh         { get; set; }
        public int      CountingWIP      { get; set; }
        public int      OtsQtyToday      { get; set; }
        public decimal  OtsPercentToday  { get; set; }
        public int      CurrentEOW       { get; set; }
    }
}
