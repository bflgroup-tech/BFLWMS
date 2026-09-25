using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Core;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// CDC Store Allocation — the second half of the future-LPMDt story.
///
/// PO allocation parks lines dated two or more months out at the holding bucket
/// StoreID 'CDC'. Nothing released them again. This does: pick a (ContNo, OraPONo),
/// read its counting boxes from racks.dbo.whboxitems where PalletType = 'CD', and
/// run the same algorithm to spread that stock across stores.
///
/// The algorithm itself is not here — ContainerAllocationService.ProcessCdcAllocationAsync
/// drives the shared engine. This service owns everything around it: the PO picker,
/// validation, persistence into the dedicated CDC tables, status, delete and the
/// status report.
///
/// NOT the same thing as CdcBoxAllocationService, despite the name. That one
/// redistributes the pooled UAE DC eligible SOH by ItemCode/BoxNo and never looks at
/// a container or a PO.
///
/// Storage is deliberately separate from WMS_Cont_Allocation_Header /
/// WMS_ContAllocationData, which six other services read (the Azure data sync, box
/// building, item encoding, Pass 5, open container, OTS). The cost of that isolation:
/// a CDC allocation does not sync to Azure and does not drive box building until a
/// consumer is deliberately wired up.
/// </summary>
public class CdcStoreAllocationService(
    IOnPremConnectionResolver resolver,
    ContainerAllocationService allocSvc,
    ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;

    private const string RunOptionTag = nameof(RunOption.FillMinMinPlusOthers);
    private const string AllocTypeCdc = "CDC";

    /// <summary>How an LPM month is labelled — matches CdcBoxAllocationService.</summary>
    public static string LpmLabel(DateTime d) =>
        d.ToString("MMM-yyyy", CultureInfo.InvariantCulture);

    private static string WithConnectTimeout(string cs) =>
        new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds }.ConnectionString;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    // ===================== PO picker =====================

    /// <summary>
    /// The (ContNo, OraPONo) combinations on this container that carry CD stock.
    ///
    /// Nothing is joined in before the aggregate: whboxitems is finer than
    /// (box, item), and joining vupc_subclass — which holds several rows per
    /// itemcode — before summing would multiply Qty.
    /// </summary>
    public async Task<List<CdcPoOption>> GetCdcPoOptionsAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        contno = contno.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcPoOption>(new CommandDefinition(@"
            SELECT ContNo   = w.ContNo,
                   OraPONo  = w.OraPoNo,
                   Items    = COUNT(DISTINCT w.ItemCode),
                   Qty      = CAST(SUM(CAST(ISNULL(w.Qty, 0) AS BIGINT)) AS INT),
                   MinLpmDt = MIN(w.LPMDt),
                   MaxLpmDt = MAX(w.LPMDt)
              FROM racks.dbo.whboxitems w WITH (NOLOCK)
             WHERE w.ContNo     = @c
               AND w.PalletType = @pt
               AND w.OraPoNo   IS NOT NULL
               AND LTRIM(RTRIM(w.OraPoNo)) <> ''
             GROUP BY w.ContNo, w.OraPoNo
            HAVING SUM(CAST(ISNULL(w.Qty, 0) AS BIGINT)) > 0
             ORDER BY w.OraPoNo",
            new { c = contno, pt = ContainerAllocationService.CdcPalletType },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ===================== Validation =====================

    /// <summary>
    /// A CDC-specific validator rather than a mode flag on ValidateAsync.
    ///
    /// Four of that validator's steps would fail 100% of CDC runs, because the
    /// container having been completed and synced is precisely how the stock reached
    /// the DC: WmsBuildingCompletion, the Azure already-synced check,
    /// WMSContBuildScanData and WmsOpenBox. Bolting always-fail branches into the
    /// daily gate is how a gate gets quietly weakened, so this is a sibling.
    ///
    /// The three-way qty match is dropped too — CD stock is a remainder of the order,
    /// so usaorgfile_LPM / USAOrgFile / vUSAOrder cannot agree with it by design.
    /// The OTS chain (OTS generated today, weekly-sales freshness) is kept verbatim,
    /// because CDC consumes OTS exactly as a PO run does.
    /// </summary>
    public async Task<ContainerAllocationValidationResult> ValidateCdcAsync(
        string contno, string oraPoNo,
        IProgress<AllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        const int TOTAL = 5;
        var steps = new List<ValidationStep>();
        contno  = (contno  ?? "").Trim();
        oraPoNo = (oraPoNo ?? "").Trim();

        if (contno.Length == 0 || oraPoNo.Length == 0)
        {
            steps.Add(new ValidationStep("Container and PO supplied", false,
                "Pick both a container and a PO number before validating."));
            return new ContainerAllocationValidationResult(false, steps);
        }

        await using var c = OpenOnPremBackup();

        // 1. CD stock exists for this (ContNo, PO). Replaces the three-way qty match.
        progress?.Report(new AllocationProgress(1, TOTAL, "Validating: CDC stock present"));
        var cd = await c.QueryFirstAsync<(int Lines, long Qty)>(new CommandDefinition(@"
            SELECT Lines = COUNT(*),
                   Qty   = CAST(ISNULL(SUM(CAST(ISNULL(w.Qty,0) AS BIGINT)), 0) AS BIGINT)
              FROM racks.dbo.whboxitems w WITH (NOLOCK)
             WHERE w.ContNo = @c AND w.PalletType = @pt AND w.OraPoNo = @po",
            new { c = contno, po = oraPoNo, pt = ContainerAllocationService.CdcPalletType },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var cdOk = cd.Qty > 0;
        steps.Add(new ValidationStep(
            $"CDC stock present (racks.dbo.whboxitems, PalletType '{ContainerAllocationService.CdcPalletType}')",
            cdOk,
            cdOk ? $"{cd.Lines:N0} box line(s), {cd.Qty:N0} pcs."
                 : $"No PalletType '{ContainerAllocationService.CdcPalletType}' stock for {contno} / PO {oraPoNo}."));
        if (!cdOk) return new ContainerAllocationValidationResult(false, steps);

        // 2. Not already CDC-allocated. The database enforces this too (unique index
        //    on the header); this step exists so the normal case gets a readable
        //    message instead of a constraint violation.
        progress?.Report(new AllocationProgress(2, TOTAL, "Validating: not already allocated"));
        var prior = await GetCdcStatusAsync(contno, oraPoNo, ct);
        var notAllocated = !prior.HasFinal;
        steps.Add(new ValidationStep(
            "Not already CDC-allocated",
            notAllocated,
            notAllocated
                ? null
                : $"{contno} / PO {oraPoNo} was already CDC-allocated as batch {prior.BatchNo} "
                  + $"on {prior.ProcessedTS:dd/MM/yyyy HH:mm} by {prior.ProcessedBy} "
                  + $"({prior.TotalQty:N0} pcs over {prior.Rows:N0} row(s)). Delete that allocation first."));
        if (!notAllocated) return new ContainerAllocationValidationResult(false, steps);

        // 3. Sanity: CD stock cannot exceed what the PO ordered. If it does, the
        //    PalletType or OraPoNo stamping on those boxes is wrong, and allocating
        //    from it would place stock that does not exist.
        progress?.Report(new AllocationProgress(3, TOTAL, "Validating: CDC qty vs ordered qty"));
        var ordered = await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            @"SELECT CAST(ISNULL(SUM(CAST(ISNULL(orgqty,0) AS BIGINT)), 0) AS BIGINT)
                FROM usa.dbo.usaorgfile_LPM WITH (NOLOCK)
               WHERE ContNo = @c AND OraPONo = @po",
            new { c = contno, po = oraPoNo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) ?? 0;
        var qtyOk = ordered > 0 && cd.Qty <= ordered;
        steps.Add(new ValidationStep(
            "CDC qty within the PO's ordered qty",
            qtyOk,
            qtyOk ? $"{cd.Qty:N0} of {ordered:N0} ordered."
                  : ordered == 0
                      ? $"usa.dbo.usaorgfile_LPM has no lines for {contno} / PO {oraPoNo} — check the PO number."
                      : $"CDC stock ({cd.Qty:N0}) exceeds the PO's ordered qty ({ordered:N0}). "
                        + "PalletType or OraPoNo is mis-stamped on those boxes."));
        if (!qtyOk) return new ContainerAllocationValidationResult(false, steps);

        // 4. OTS generated today — the store universe and every tier decision come
        //    from today's run. Same gate the PO path applies to the OTS algorithms.
        progress?.Report(new AllocationProgress(4, TOTAL, "Validating: OTS Generated today"));
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        var otsToday = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(1) FROM dbo.WmsOtsPoAllocationRun WITH (NOLOCK) WHERE OTSDate = @dt",
            new { dt = todayGst }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        var otsOk = otsToday > 0;
        steps.Add(new ValidationStep(
            "OTS for PO Allocation generated today",
            otsOk,
            otsOk ? null
                  : $"OTS for PO Allocation has not been Generated today ({todayGst:dd/MM/yyyy} GST). "
                    + "Go to OTS for PO Allocation → Generate first, then re-run Process."));
        if (!otsOk) return new ContainerAllocationValidationResult(false, steps);

        // 5. Weekly sales freshness — reused verbatim. Volume Group grading averages
        //    the twelve weeks before the OTS anchor, so a missing anchor-1 leaves
        //    every grade one week stale while the gate above still passes.
        progress?.Report(new AllocationProgress(5, TOTAL, "Validating: weekly sales received"));
        var sales = await allocSvc.GetWeeklySalesStatusAsync(ct);
        steps.Add(new ValidationStep(
            "Weekly sales received for last week",
            sales.Ok,
            sales.Summary));
        if (!sales.Ok) return new ContainerAllocationValidationResult(false, steps);

        return new ContainerAllocationValidationResult(true, steps);
    }

    // ===================== Status / delete =====================

    public async Task<CdcAllocationStatus> GetCdcStatusAsync(string contno, string oraPoNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno) || string.IsNullOrWhiteSpace(oraPoNo))
            return CdcAllocationStatus.None;

        await using var c = OpenOnPremBackup();
        var row = await c.QueryFirstOrDefaultAsync<CdcAllocationStatus>(new CommandDefinition(@"
            SELECT TOP 1
                   HasFinal    = CAST(1 AS BIT),
                   Rows        = ISNULL(h.RowCount1, 0),
                   TotalQty    = ISNULL(h.TotalQty, 0),
                   BatchNo     = h.BatchNo,
                   ProcessedTS = h.ProcessedTS,
                   ProcessedBy = h.ProcessedBy,
                   ApprovedDt  = h.ApprovedDt
              FROM LPMSIM.dbo.WMS_CDC_Allocation_Header h WITH (NOLOCK)
             WHERE h.ContNo = @c AND h.OraPONo = @po
             ORDER BY h.BatchNo DESC",
            new { c = contno.Trim(), po = oraPoNo.Trim() },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return row ?? CdcAllocationStatus.None;
    }

    /// <summary>
    /// The saved rows for a (ContNo, OraPONo), for reloading an existing allocation
    /// into the page without re-running it.
    /// </summary>
    public async Task<List<AllocationRow>> LoadCdcFinalAsync(string contno, string oraPoNo, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<AllocationRow>(new CommandDefinition(@"
            SELECT Contno           = d.ContNo,
                   OraPONo          = d.ORAPONo,
                   ItemCode         = d.Itemcode,
                   ItemName         = d.Itemname,
                   d.Brand,
                   PoQty            = ISNULL(d.POQty, 0),
                   d.StoreID,
                   StoreName        = NULL,
                   d.Country,
                   d.Division,
                   VolumeGroup      = ISNULL(d.GroupCode, ''),
                   SkuMax           = ISNULL(d.SkuMax, 0),
                   AllocQty         = ISNULL(d.AllocatedQty, 0),
                   MerchNeedMonth   = 0,
                   DivCode          = ISNULL(d.DivCode, 0),
                   RoundRobinExtra  = 0,
                   LPM              = NULL,
                   d.LPMDt,
                   d.OTS,
                   d.Season, d.Style, d.[Size], d.Department, d.SalesPrice,
                   PalletType       = d.ResultType,
                   PrevAllocatedQty = ISNULL(d.PrevAllocatedQty, 0),
                   d.PriorityRank, d.MnwToday, d.Phase2Qty,
                   d.Pass1Qty, d.Pass2Qty, d.Pass3Qty, d.Pass4Qty,
                   d.RatioSkuMax, d.AvgOtsPercent, d.OtsQtyToday, d.TgtEOM, d.RawSkuMax,
                   d.SkuMaxBand, d.AvgOtsMin, d.AvgOtsMax, d.InitialOtsPct,
                   d.Soh, d.RunningOtsQty, d.MinMinCoverPct
              FROM LPMSIM.dbo.WMS_CDCAllocationData d WITH (NOLOCK)
              JOIN LPMSIM.dbo.WMS_CDC_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo
             WHERE h.ContNo = @c AND h.OraPONo = @po
             ORDER BY d.Itemcode, d.StoreID",
            new { c = contno.Trim(), po = oraPoNo.Trim() },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Delete a CDC allocation so the (ContNo, OraPONo) can be run again. One
    /// statement: the detail and blocked tables cascade off the header. The
    /// PO-scoped audit rows are cleared too, matching what the engine would
    /// otherwise clear on the next run.
    /// </summary>
    public async Task ResetCdcAllocationAsync(string contno, string oraPoNo, CancellationToken ct = default)
    {
        contno  = (contno  ?? "").Trim();
        oraPoNo = (oraPoNo ?? "").Trim();
        if (contno.Length == 0 || oraPoNo.Length == 0) return;

        var status = await GetCdcStatusAsync(contno, oraPoNo, ct);
        if (status.IsApproved)
            throw new InvalidOperationException(
                $"{contno} / PO {oraPoNo} was approved on {status.ApprovedDt:dd/MM/yyyy HH:mm} and cannot be deleted.");

        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_CDC_Allocation_Header WHERE ContNo = @c AND OraPONo = @po",
            new { c = contno, po = oraPoNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WmsPlanningFlag   WHERE ContNo = @c AND PONo = @po",
            new { c = contno, po = oraPoNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WmsAllocationTrace WHERE ContNo = @c AND PONo = @po",
            new { c = contno, po = oraPoNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.Pass1ByPass        WHERE ContNo = @c AND PONo = @po",
            new { c = contno, po = oraPoNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ===================== Save =====================

    /// <summary>
    /// Persist a CDC run. Unlike the PO save there is NO delete-prior-batch step —
    /// refusing a second run is the whole point, so a re-run has to go through
    /// Delete first.
    ///
    /// The unique index on (ContNo, OraPONo) is the real enforcement; the
    /// validation pre-check is only for a readable message. Two operators on the
    /// same PO both pass that check before either writes, so the insert below
    /// translates the constraint violation rather than letting it surface raw.
    /// </summary>
    public async Task<int> SaveCdcFinalAsync(
        string genCountry, string contno, string oraPoNo, string allocationCountries, string? warehouse,
        List<AllocationRow> rows, List<BlockedItemRow>? blocked, int sourceQty,
        IProgress<AllocationProgress>? progress = null, CancellationToken ct = default)
    {
        contno  = contno.Trim();
        oraPoNo = oraPoNo.Trim();

        await using var c = OpenOnPremBackup();

        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: creating header row"));
        var totalQty = rows.Sum(r => r.AllocQty);
        int batchNo;
        try
        {
            batchNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(@"
                INSERT INTO LPMSIM.dbo.WMS_CDC_Allocation_Header
                    (ContNo, OraPONo, Warehouse, GenCountry, Country, RunOption,
                     AllocType, SourceTable, PalletType, RowCount1, TotalQty, SourceQty, ProcessedBy)
                VALUES (@c, @po, @wh, @gc, @ac, @ro, @at, @st, @pt, @rc, @tq, @sq, @u);
                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new { c = contno, po = oraPoNo, wh = warehouse, gc = genCountry, ac = allocationCountries,
                      ro = RunOptionTag, at = AllocTypeCdc, st = "racks.dbo.whboxitems",
                      pt = ContainerAllocationService.CdcPalletType,
                      rc = rows.Count, tq = totalQty, sq = sourceQty, u = user.Name },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new InvalidOperationException(
                $"{contno} / PO {oraPoNo} was CDC-allocated by someone else while this run was in progress. "
                + "Reload the page to see that allocation.", ex);
        }

        // Blocked rows. BlockedItemRow carries no PO — the whole run is one PO, so
        // stamp it here rather than widening a positional record built at five sites
        // inside the engine loop.
        if (blocked is { Count: > 0 })
        {
            progress?.Report(new AllocationProgress(0, rows.Count, $"Saving: {blocked.Count:N0} blocked row(s)"));
            var bdt = new DataTable();
            bdt.Columns.Add("BatchNo",     typeof(int));
            bdt.Columns.Add("ContNo",      typeof(string));
            bdt.Columns.Add("ORAPONo",     typeof(string));
            bdt.Columns.Add("Country",     typeof(string));
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
                bdt.Rows.Add(
                    batchNo, b.Contno, oraPoNo, b.Country, RunOptionTag,
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

            using var bulkBlk = new SqlBulkCopy(c)
            {
                DestinationTableName = "LPMSIM.dbo.WMS_CDCAllocationBlocked",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (DataColumn col in bdt.Columns) bulkBlk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulkBlk.WriteToServerAsync(bdt, ct);
        }

        progress?.Report(new AllocationProgress(0, rows.Count, "Saving: bulk insert"));
        var now     = DateTime.UtcNow.AddHours(4);
        var trnDate = now.Date;
        var time1   = now.TimeOfDay;

        var dt = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
        {
            ("BatchNo", typeof(int)), ("ContNo", typeof(string)), ("ORAPONo", typeof(string)),
            ("Country", typeof(string)), ("TrnDate", typeof(DateTime)), ("Time1", typeof(TimeSpan)),
            ("UPC", typeof(string)), ("Itemcode", typeof(string)), ("Barcode", typeof(string)),
            ("GroupCode", typeof(string)), ("POQty", typeof(int)), ("SkuMax", typeof(int)),
            ("AllocatedQty", typeof(int)), ("PrevAllocatedQty", typeof(int)), ("QtyIssue", typeof(int)),
            ("StoreID", typeof(string)), ("TcmContno", typeof(string)), ("Itemname", typeof(string)),
            ("BuildingCategory", typeof(string)), ("LPMDt", typeof(DateTime)), ("Division", typeof(string)),
            ("Brand", typeof(string)), ("DivCode", typeof(int)), ("Department", typeof(string)),
            ("Season", typeof(string)), ("Style", typeof(string)), ("Size", typeof(string)),
            ("SalesPrice", typeof(decimal)), ("ResultType", typeof(string)), ("FinalResult", typeof(string)),
            ("Remarks", typeof(string)), ("OTS", typeof(double)), ("PriorityRank", typeof(int)),
            ("MnwToday", typeof(int)), ("Phase2Qty", typeof(int)), ("Pass1Qty", typeof(int)),
            ("Pass2Qty", typeof(int)), ("Pass3Qty", typeof(int)), ("Pass4Qty", typeof(int)),
            ("RatioSkuMax", typeof(int)), ("AvgOtsPercent", typeof(decimal)), ("SkuMaxBand", typeof(string)),
            ("AvgOtsMin", typeof(decimal)), ("AvgOtsMax", typeof(decimal)), ("InitialOtsPct", typeof(decimal)),
            ("Soh", typeof(int)), ("RunningOtsQty", typeof(int)), ("OtsQtyToday", typeof(int)),
            ("TgtEOM", typeof(int)), ("RawSkuMax", typeof(int)), ("MinMinCoverPct", typeof(decimal)),
        })
            dt.Columns.Add(name, type);

        foreach (var r in rows)
            dt.Rows.Add(
                batchNo, r.Contno, oraPoNo, r.Country, trnDate, time1,
                r.ItemCode, r.ItemCode, r.ItemCode,          // UPC = Barcode = ItemCode
                r.VolumeGroup,
                r.PoQty, r.SkuMax, r.AllocQty, r.PrevAllocatedQty, 0,
                r.StoreID, r.Contno,
                (object?)r.ItemName ?? DBNull.Value,
                (object?)r.Division ?? DBNull.Value,          // BuildingCategory = Division
                (object?)r.LPMDt ?? DBNull.Value,
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

        c.ChangeDatabase("LPMSIM");
        using var bulk = new SqlBulkCopy(c)
        {
            DestinationTableName = "dbo.WMS_CDCAllocationData",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
            NotifyAfter          = 500,
        };
        bulk.SqlRowsCopied += (_, e) =>
            progress?.Report(new AllocationProgress((int)e.RowsCopied, rows.Count, "Saving to LPMSIM"));
        foreach (DataColumn col in dt.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        progress?.Report(new AllocationProgress(rows.Count, rows.Count, "Saving: done"));
        return batchNo;
    }

    // ===================== Report =====================

    /// <summary>
    /// Allocated vs pending CDC stock, one row per (ContNo, OraPONo, LPM month).
    ///
    /// Pending is a LEFT JOIN miss against the CDC header — the same anti-join shape
    /// PendingForCountingService uses — and Status stays NULL for it, matching the
    /// bulk queue's convention so the UI can render `Status ?? "Pending"`.
    ///
    /// The allocated totals come from a pre-aggregated subquery rather than a join
    /// onto the box rows: joining detail to detail would multiply both sides.
    /// </summary>
    public async Task<List<CdcAllocationStatusRow>> GetCdcAllocationStatusAsync(
        DateTime? lpmFrom, DateTime? lpmTo, string? contNo, bool pendingOnly,
        CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<CdcAllocationStatusRow>(new CommandDefinition(@"
            ;WITH cd AS (
                SELECT w.ContNo, w.OraPoNo, w.LPMDt,
                       Items = COUNT(DISTINCT w.ItemCode),
                       CdQty = CAST(SUM(CAST(ISNULL(w.Qty,0) AS BIGINT)) AS INT)
                  FROM racks.dbo.whboxitems w WITH (NOLOCK)
                 WHERE w.PalletType = @pt
                   AND w.OraPoNo IS NOT NULL AND LTRIM(RTRIM(w.OraPoNo)) <> ''
                   AND (@cn IS NULL OR w.ContNo = @cn)
                   AND (@lf IS NULL OR w.LPMDt >= @lf)
                   AND (@lt IS NULL OR w.LPMDt <= @lt)
                 GROUP BY w.ContNo, w.OraPoNo, w.LPMDt
                HAVING SUM(CAST(ISNULL(w.Qty,0) AS BIGINT)) > 0
            ), alloc AS (
                SELECT h.ContNo, h.OraPONo, h.BatchNo, h.ProcessedTS, h.ProcessedBy, h.ApprovedDt,
                       AllocatedQty  = ISNULL(h.TotalQty, 0),
                       AllocatedRows = ISNULL(h.RowCount1, 0)
                  FROM LPMSIM.dbo.WMS_CDC_Allocation_Header h WITH (NOLOCK)
            )
            SELECT ContNo        = cd.ContNo,
                   OraPONo       = cd.OraPoNo,
                   LpmDt         = cd.LPMDt,
                   Items         = cd.Items,
                   CdQty         = cd.CdQty,
                   Status        = CASE WHEN a.BatchNo IS NULL THEN NULL ELSE 'Allocated' END,
                   BatchNo       = a.BatchNo,
                   AllocatedQty  = a.AllocatedQty,
                   AllocatedRows = a.AllocatedRows,
                   ProcessedTS   = a.ProcessedTS,
                   ProcessedBy   = a.ProcessedBy,
                   ApprovedDt    = a.ApprovedDt
              FROM cd
              LEFT JOIN alloc a ON a.ContNo = cd.ContNo AND a.OraPONo = cd.OraPoNo
             WHERE (@pendingOnly = 0 OR a.BatchNo IS NULL)
             ORDER BY cd.LPMDt, cd.ContNo, cd.OraPoNo",
            new { pt = ContainerAllocationService.CdcPalletType,
                  cn = string.IsNullOrWhiteSpace(contNo) ? null : contNo.Trim(),
                  lf = lpmFrom, lt = lpmTo,
                  pendingOnly = pendingOnly ? 1 : 0 },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>The LPM months that carry CD stock, for the report's month picker.</summary>
    public async Task<List<DateTime>> GetCdcLpmMonthsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<DateTime>(new CommandDefinition(@"
            SELECT DISTINCT DATEFROMPARTS(YEAR(w.LPMDt), MONTH(w.LPMDt), 1)
              FROM racks.dbo.whboxitems w WITH (NOLOCK)
             WHERE w.PalletType = @pt AND w.LPMDt IS NOT NULL
             ORDER BY 1",
            new { pt = ContainerAllocationService.CdcPalletType },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
