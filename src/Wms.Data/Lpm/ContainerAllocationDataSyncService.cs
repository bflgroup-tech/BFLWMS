using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Data;

namespace Wms.Data.Lpm;

/// <summary>
/// Copies a Container Allocation's detail rows (LPMSIM.dbo.WMS_ContAllocationData
/// for an approved batch) to either:
///   • the Azure WMS DB (mirror table dbo.WMS_ContAllocationData), or
///   • the on-prem WmsProductionDb (legacy table online.dbo.PhotoCheckingResult).
///
/// Sync is gated on the actual mirror content, not on prior log entries: the
/// allocation copy is skipped only if the destination table (dbo.WMS_ContAllocationData
/// on Azure, online.dbo.PhotoCheckingResult on-prem) already has rows for this
/// ContNo. That way a re-approved batch can be re-shipped after the previous
/// mirror rows have been cleared. The KNB gate follows the same principle
/// (dbo.WmsKNBBoxes for this Country+ContNo).
///
/// Activity is logged to Azure WMS DB.WMS_ContAllocationDataSync_Log.
/// </summary>
public class ContainerAllocationDataSyncService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
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

    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsProductionDbConnectionString()));
        c.Open();
        return c;
    }

    /// <summary>Opens the per-country on-prem connection (Bahrain/KSA/Kuwait/MALAYSIA/Qatar).
    /// Throws InvalidOperationException if {Country}_DB_ConnectionString is not configured —
    /// caller should catch and convert to a "Skipped" log entry for countries we don't
    /// have a connection for (e.g. ECOM, Ex2Locations, OMAN today).</summary>
    private SqlConnection OpenCountry(string country)
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetCountryConnectionString(country)));
        c.Open();
        return c;
    }

    // ===================== Read-side =====================

    /// <summary>Approved containers, newest approval first. Joins the sync log to
    /// flag containers that have already been synced (per Q4 — any prior sync
    /// blocks the row).</summary>
    public async Task<List<ApprovedContnoRow>> GetApprovedContnosAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Approved Headers + their detail-row counts come from LPMSIM. The
        // sync-log lookup happens on Azure WMS, so we two-step: get the LPMSIM
        // rows first, then mark already-synced via the log.
        var rows = (await c.QueryAsync<(string ContNo, int BatchCount, int TotalAllocatedQty, DateTime LatestApprovedDt)>(new CommandDefinition(@"
            SELECT h.ContNo,
                   COUNT(DISTINCT h.BatchNo)                  AS BatchCount,
                   ISNULL(SUM(h.TotalQty), 0)                 AS TotalAllocatedQty,
                   MAX(h.ApprovedDt)                          AS LatestApprovedDt
              FROM LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK)
             WHERE h.ApprovedDt IS NOT NULL
             GROUP BY h.ContNo
             ORDER BY MAX(h.ApprovedDt) DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (rows.Count == 0) return new();

        HashSet<string> synced;
        await using (var w = OpenWms())
        {
            var contnos = rows.Select(r => r.ContNo).Distinct().ToArray();
            synced = (await w.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT ContNo
                  FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
                 WHERE ContNo IN @cs
                   AND Destination IN ('AzureWmsDb','WmsProductionDb')",
                new { cs = contnos }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return rows.Select(r => new ApprovedContnoRow(
            r.ContNo, r.BatchCount, r.TotalAllocatedQty, r.LatestApprovedDt,
            synced.Contains(r.ContNo))).ToList();
    }

    /// <summary>Last N rows from the sync log. Optional ContNo substring filter,
    /// optional Destination filter. When destinations is null/empty, returns
    /// all rows (no destination filter applied).</summary>
    /// <remarks>The destination clause is built at runtime — Dapper's list
    /// expansion of @dests would clash with a "@dests IS NULL OR …" guard
    /// (the IN list gets substituted into the IS NULL check too, producing
    /// "(@p1, @p2, …) IS NULL OR …" which SQL Server rejects with
    /// "non-boolean type … near ','").</remarks>
    public async Task<List<DataSyncActivityRow>> GetRecentActivityAsync(
        int top = 50, string? searchContno = null, string[]? destinations = null,
        CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(searchContno) ? null : "%" + searchContno.Trim() + "%";
        var destClause = (destinations is { Length: > 0 })
            ? "AND Destination IN @dests"
            : "";
        var sql = $@"
            SELECT TOP ({top})
                   SyncId, ContNo, BatchNo, Destination, TotalAllocatedQty,
                   Status, ErrorMessage, SyncedBy, SyncedTS, Origin
              FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
             WHERE (@s IS NULL OR ContNo LIKE @s)
               {destClause}
             ORDER BY SyncedTS DESC, SyncId DESC";
        await using var c = OpenWms();
        var rows = await c.QueryAsync<DataSyncActivityRow>(new CommandDefinition(
            sql, new { s = like, dests = destinations },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Destination keys (enum names as strings) corresponding to the
    /// three master-data sync flows. Used to filter Recent Activity tables on
    /// the Container vs Master sync pages.</summary>
    public static readonly string[] MasterDestinations = new[]
    {
        nameof(DataSyncDestination.WMSDataSettings),
        nameof(DataSyncDestination.WMSPalletType),
        nameof(DataSyncDestination.WMSBrandMaster),
        nameof(DataSyncDestination.ToteIDMaster),
        nameof(DataSyncDestination.WmsProdUsedTotes),
    };

    public static readonly string[] ContainerDestinations = new[]
    {
        nameof(DataSyncDestination.AzureWmsDb),
        nameof(DataSyncDestination.WmsProductionDb),
        nameof(DataSyncDestination.WmsKnbBoxes),
        nameof(DataSyncDestination.WmsProdDbToAzure),
    };

    /// <summary>True if the destination mirror already holds rows for this ContNo.
    /// The gate is now based on the actual mirror content (dbo.WMS_ContAllocationData
    /// for Azure, online.dbo.PhotoCheckingResult for on-prem) so that after a
    /// re-approval — where the Azure rows have been cleared / never populated — the
    /// re-sync is allowed.</summary>
    public async Task<bool> IsAlreadySyncedAsync(string contno, DataSyncDestination destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return false;
        switch (destination)
        {
            case DataSyncDestination.AzureWmsDb:
            {
                await using var c = OpenWms();
                var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                    @"SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return hit == 1;
            }
            case DataSyncDestination.WmsProductionDb:
            {
                await using var c = OpenWmsProductionDb();
                var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                    @"SELECT TOP 1 1 FROM online.dbo.PhotoCheckingResult WITH (NOLOCK) WHERE ContNo = @c",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return hit == 1;
            }
            case DataSyncDestination.WmsProdDbToAzure:
            {
                // Reverse pull lands rows into the SAME table as the forward push
                // (dbo.WMS_ContAllocationData). Gate is identical to AzureWmsDb:
                // any rows for this ContNo blocks a re-pull. Operator must clear
                // the ContNo's rows first if they want to re-import.
                await using var c = OpenWms();
                var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
                    @"SELECT TOP 1 1 FROM dbo.WMS_ContAllocationData WITH (NOLOCK) WHERE ContNo = @c",
                    new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                return hit == 1;
            }
            default:
                return false;
        }
    }

    /// <summary>True if dbo.WmsKNBBoxes already has rows for this Country + ContNo
    /// — used to skip the KNB pull on subsequent syncs.</summary>
    public async Task<bool> IsKnbBoxesPulledAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return false;
        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT TOP 1 1 FROM dbo.WmsKNBBoxes WITH (NOLOCK)
               WHERE Country = @country AND Contno = @c",
            new { country = user.Country, c = contno },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return hit == 1;
    }

    // ===================== Write-side =====================

    /// <summary>Sync entry point — runs the allocation copy AND the KNB-boxes
    /// pull for the same ContNo. The two have independent gates and produce
    /// their own log rows; the UI sees both in Recent Activity.
    /// The WmsProdDbToAzure destination is a REVERSE pull (WMSPROD ->
    /// Azure mirror) — it skips both the forward allocation copy and the KNB
    /// pull, which are source-side flows irrelevant to that direction.</summary>
    public async Task<DataSyncResult> SyncAsync(string contno, DataSyncDestination destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno))
            return new DataSyncResult(false, "Container number is required.", null, 0);
        contno = contno.Trim();

        if (destination == DataSyncDestination.WmsProdDbToAzure)
            return await TryReversePullFromWmsProdDbAsync(contno, ct);

        var alloc = await TryCopyAllocationAsync(contno, destination, ct);
        var knb   = await TryCopyKnbBoxesAsync(contno, ct);

        // Both produce log rows. Combine for the page banner.
        var ok      = alloc.Ok || knb.Ok;
        var parts   = new[] { alloc.Message, knb.Message }.Where(m => !string.IsNullOrEmpty(m));
        return new DataSyncResult(
            Ok: ok,
            Message: string.Join(" | ", parts),
            SyncId: alloc.SyncId ?? knb.SyncId,
            RowsCopied: alloc.RowsCopied + knb.RowsCopied);
    }

    // ----- reverse pull: WMSPROD -> Azure dbo.WMS_ContAllocationData -----
    /// <summary>Read online.dbo.PhotoCheckingResult (filtered by ContNo) from the
    /// on-prem WmsProductionDb, enrich Color/Gender/HsCode/Brand from
    /// usa.dbo.usaorgfile and Class/Family/Subclass from
    /// datareporting.dbo.vupc_subclass + SubclassMaster (both on OnPremBackup),
    /// then SqlBulkCopy the rows into Azure dbo.WMS_ContAllocationData so LPM
    /// Manual Building can route scans for store sorting. Gated on the target
    /// having any rows for this ContNo — clear them to re-pull.
    /// Country is hardcoded 'UAE'. BatchNo is left NULL (no allocation header).
    /// AllocatedQty = POQty = source.Qty; Result = source.FinalResult.
    /// PrevAllocatedQty / Phase2Qty / SkuMax / OTS / PriorityRank / MnwToday /
    /// DivCode / Size are set to defaults (0 or NULL) because the source
    /// legacy PhotoCheckingResult has no equivalent.</summary>
    private async Task<DataSyncResult> TryReversePullFromWmsProdDbAsync(string contno, CancellationToken ct)
    {
        var dest = DataSyncDestination.WmsProdDbToAzure;

        if (await IsAlreadySyncedAsync(contno, dest, ct))
        {
            var skipId = await WriteLogRowAsync(
                contno, null, dest, 0,
                status: "Skipped",
                error: $"Azure dbo.WMS_ContAllocationData already has rows for {contno}.", ct);
            return new DataSyncResult(false,
                $"Reverse pull: Azure dbo.WMS_ContAllocationData already has rows for {contno} — skipped. Clear the ContNo's rows first.",
                skipId, 0);
        }

        // 1. Read raw source rows from WMSPROD.
        List<ReverseSourceRow> sourceRows;
        try
        {
            await using var src = OpenWmsProductionDb();
            sourceRows = (await src.QueryAsync<ReverseSourceRow>(new CommandDefinition(@"
                SELECT ContNo, TrnDate, Time1, UPC, Itemcode, GroupCode, Season, Department, Division,
                       FinalResult, ResultType, Qty, QtyIssue, Itemname, Barcode, SalesPrice,
                       TcmContno, BuildingCategory, LPMDt, LPMBoxNO, ORAPONo, Style, Remarks, StoreId
                  FROM online.dbo.PhotoCheckingResult WITH (NOLOCK)
                 WHERE ContNo = @c",
                new { c = contno },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync(contno, null, dest, 0,
                "Failed", $"Reading online.dbo.PhotoCheckingResult failed: {ex.Message}", ct);
            return new DataSyncResult(false, $"Reverse pull read failed: {ex.Message}", failId, 0);
        }

        if (sourceRows.Count == 0)
        {
            var emptyId = await WriteLogRowAsync(contno, null, dest, 0,
                "Empty", $"online.dbo.PhotoCheckingResult returned no rows for ContNo = {contno}.", ct);
            return new DataSyncResult(true, $"Reverse pull: WMSPRODDB has no rows for {contno}.", emptyId, 0);
        }

        // 2. Enrichment lookups on OnPremBackup — batch by distinct Itemcode.
        //    We only look up the enrichment fields that PhotoCheckingResult doesn't
        //    carry: Color/Gender/HsCode/Brand (usaorgfile) + Class/Family/Subclass
        //    (vupc_subclass + SubclassMaster). Chunked to 1000 params to stay under
        //    the 2100 sqlparameter limit.
        var itemcodes = sourceRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Itemcode))
            .Select(r => r.Itemcode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var orgLookup      = new Dictionary<string, OrgEnrichmentRow>(StringComparer.OrdinalIgnoreCase);
        var subclassLookup = new Dictionary<string, SubclassEnrichmentRow>(StringComparer.OrdinalIgnoreCase);

        if (itemcodes.Length > 0)
        {
            try
            {
                await using var opb = OpenOnPremBackup();
                const int chunkSize = 1000;
                for (int i = 0; i < itemcodes.Length; i += chunkSize)
                {
                    var chunk = itemcodes.Skip(i).Take(chunkSize).ToArray();

                    // usa.dbo.usaorgfile — most-recent row per Itemcode for this ContNo.
                    // vendor is the Brand (per usaorgfile convention used elsewhere in
                    // ContainerAllocationService). If ContNo has no matching org row,
                    // fall back to any TOP-1 row for the Itemcode across containers.
                    var orgRows = await opb.QueryAsync<OrgEnrichmentRow>(new CommandDefinition(@"
                        SELECT o.ItemCode, o.color AS Color, o.GENDER AS Gender, o.hscode AS HsCode, o.vendor AS Brand
                          FROM (
                              SELECT uo.ItemCode, uo.color, uo.GENDER, uo.hscode, uo.vendor,
                                     ROW_NUMBER() OVER (
                                         PARTITION BY uo.ItemCode
                                         ORDER BY CASE WHEN uo.ContNo = @c THEN 0 ELSE 1 END,
                                                  uo.TrnDate DESC
                                     ) AS rn
                                FROM usa.dbo.usaorgfile uo WITH (NOLOCK)
                               WHERE uo.ItemCode IN @codes
                          ) o
                         WHERE o.rn = 1",
                        new { c = contno, codes = chunk },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    foreach (var r in orgRows)
                        orgLookup[r.ItemCode ?? ""] = r;

                    // datareporting.dbo.vupc_subclass -> SubclassMaster (same pattern
                    // as forward-push OUTER APPLY).
                    var subRows = await opb.QueryAsync<SubclassEnrichmentRow>(new CommandDefinition(@"
                        SELECT v.itemcode AS ItemCode, sm.[Class], sm.Family, sm.Subclass
                          FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                          LEFT JOIN datareporting.dbo.SubclassMaster sm WITH (NOLOCK) ON sm.MH4ID = v.MH4ID
                         WHERE v.itemcode IN @codes",
                        new { codes = chunk },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    foreach (var r in subRows)
                        subclassLookup[r.ItemCode ?? ""] = r;
                }
            }
            catch (Exception ex)
            {
                // Enrichment failure shouldn't kill the pull — log a warning and
                // continue with the source rows carrying NULL enrichment.
                await WriteLogRowAsync(contno, null, dest, 0,
                    "PartialFailed",
                    $"Enrichment lookups on OnPremBackup failed: {ex.Message}. Proceeding with NULL Color/Gender/HsCode/Brand/Class/Family/Subclass.", ct);
                orgLookup.Clear();
                subclassLookup.Clear();
            }
        }

        // 3. Build the target DataTable (mirrors BuildAzureMirrorDataTable layout).
        var dt = BuildReversePullDataTable(sourceRows, orgLookup, subclassLookup);

        // 4. Bulk-copy to Azure dbo.WMS_ContAllocationData.
        string? writeError = null;
        try
        {
            await using var conn = OpenWms();
            using var bulk = new SqlBulkCopy(conn)
            {
                DestinationTableName = "dbo.WMS_ContAllocationData",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }
        catch (Exception ex) { writeError = ex.Message; }

        var totalQty = sourceRows.Sum(r => r.Qty ?? 0);
        var logId = await WriteLogRowAsync(contno, null, dest, totalQty,
            status: writeError is null ? "Success" : "Failed",
            error: writeError, ct);

        return writeError is null
            ? new DataSyncResult(true,
                $"Reverse pull: {sourceRows.Count:N0} row(s) copied from WMSPRODDB to Azure dbo.WMS_ContAllocationData (Country='UAE', BatchNo=NULL).",
                logId, sourceRows.Count)
            : new DataSyncResult(false,
                $"Reverse pull write to dbo.WMS_ContAllocationData failed: {writeError}",
                logId, 0);
    }

    /// <summary>Build a DataTable in dbo.WMS_ContAllocationData shape (41 cols)
    /// from raw PhotoCheckingResult rows + enrichment lookups. Defaults follow
    /// the "sensible for legacy data" rules called out in
    /// TryReversePullFromWmsProdDbAsync.</summary>
    private static System.Data.DataTable BuildReversePullDataTable(
        List<ReverseSourceRow> rows,
        Dictionary<string, OrgEnrichmentRow> orgLookup,
        Dictionary<string, SubclassEnrichmentRow> subLookup)
    {
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
        dt.Columns.Add("Phase2Qty",        typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("LPMBoxNO",         typeof(string));
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
        dt.Columns.Add("Result",           typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("OTS",              typeof(double));
        dt.Columns.Add("Color",            typeof(string));
        dt.Columns.Add("Gender",           typeof(string));
        dt.Columns.Add("HsCode",           typeof(string));
        dt.Columns.Add("Class",            typeof(string));
        dt.Columns.Add("Family",           typeof(string));
        dt.Columns.Add("Subclass",         typeof(string));
        dt.Columns.Add("PriorityRank",     typeof(int));
        dt.Columns.Add("MnwToday",         typeof(int));

        foreach (var r in rows)
        {
            var key   = r.Itemcode ?? "";
            var org   = orgLookup.TryGetValue(key, out var o) ? o : null;
            var sub   = subLookup.TryGetValue(key, out var s) ? s : null;
            var qty   = r.Qty;
            var final = r.FinalResult;

            dt.Rows.Add(
                DBNull.Value,                          // BatchNo (Q1: leave NULL)
                (object?)r.ContNo           ?? DBNull.Value,
                "UAE",                                 // Country (Q2: hardcoded)
                (object?)r.TrnDate          ?? DBNull.Value,
                (object?)r.Time1            ?? DBNull.Value,
                (object?)r.UPC              ?? DBNull.Value,
                (object?)r.Itemcode         ?? DBNull.Value,
                (object?)r.Barcode          ?? DBNull.Value,
                (object?)r.GroupCode        ?? DBNull.Value,
                (object?)qty                ?? DBNull.Value,  // POQty  = source Qty
                DBNull.Value,                          // SkuMax
                (object?)qty                ?? DBNull.Value,  // AllocatedQty = source Qty
                0,                                     // PrevAllocatedQty
                (object?)r.QtyIssue         ?? DBNull.Value,
                0,                                     // Phase2Qty
                (object?)r.StoreId          ?? DBNull.Value,
                (object?)r.TcmContno        ?? DBNull.Value,
                (object?)r.Itemname         ?? DBNull.Value,
                (object?)r.BuildingCategory ?? DBNull.Value,
                (object?)r.LPMDt            ?? DBNull.Value,
                (object?)r.LPMBoxNO         ?? DBNull.Value,
                (object?)r.ORAPONo          ?? DBNull.Value,
                (object?)r.Division         ?? DBNull.Value,
                (object?)org?.Brand         ?? DBNull.Value,
                DBNull.Value,                          // DivCode
                (object?)r.Department       ?? DBNull.Value,
                (object?)r.Season           ?? DBNull.Value,
                (object?)r.Style            ?? DBNull.Value,
                DBNull.Value,                          // Size (no equivalent on PhotoCheckingResult)
                ParseDecimalOrDbNull(r.SalesPrice),
                (object?)r.ResultType       ?? DBNull.Value,
                (object?)final              ?? DBNull.Value,
                (object?)final              ?? DBNull.Value,  // Result = FinalResult (source has no split)
                (object?)r.Remarks          ?? DBNull.Value,
                0.0,                                   // OTS
                (object?)org?.Color         ?? DBNull.Value,
                (object?)org?.Gender        ?? DBNull.Value,
                (object?)org?.HsCode        ?? DBNull.Value,
                (object?)sub?.Class         ?? DBNull.Value,
                (object?)sub?.Family        ?? DBNull.Value,
                (object?)sub?.Subclass      ?? DBNull.Value,
                DBNull.Value,                          // PriorityRank
                DBNull.Value);                         // MnwToday
        }
        return dt;
    }

    private sealed class ReverseSourceRow
    {
        public string?   ContNo           { get; set; }
        public DateTime? TrnDate          { get; set; }
        public TimeSpan? Time1            { get; set; }
        public string?   UPC              { get; set; }
        public string?   Itemcode         { get; set; }
        public string?   GroupCode        { get; set; }
        public string?   Season           { get; set; }
        public string?   Department       { get; set; }
        public string?   Division         { get; set; }
        public string?   FinalResult      { get; set; }
        public string?   ResultType       { get; set; }
        public int?      Qty              { get; set; }
        public int?      QtyIssue         { get; set; }
        public string?   Itemname         { get; set; }
        public string?   Barcode          { get; set; }
        public string?   SalesPrice       { get; set; }
        public string?   TcmContno        { get; set; }
        public string?   BuildingCategory { get; set; }
        public DateTime? LPMDt            { get; set; }
        public string?   LPMBoxNO         { get; set; }
        public string?   ORAPONo          { get; set; }
        public string?   Style            { get; set; }
        public string?   Remarks          { get; set; }
        public string?   StoreId          { get; set; }
    }

    private sealed class OrgEnrichmentRow
    {
        public string? ItemCode { get; set; }
        public string? Color    { get; set; }
        public string? Gender   { get; set; }
        public string? HsCode   { get; set; }
        public string? Brand    { get; set; }
    }

    private sealed class SubclassEnrichmentRow
    {
        public string? ItemCode { get; set; }
        public string? Class    { get; set; }
        public string? Family   { get; set; }
        public string? Subclass { get; set; }
    }

    // ----- pass 1: allocation copy (with Q4 gate) -----
    private async Task<DataSyncResult> TryCopyAllocationAsync(string contno, DataSyncDestination destination, CancellationToken ct)
    {
        if (await IsAlreadySyncedAsync(contno, destination, ct))
        {
            var skipId = await WriteLogRowAsync(
                contno, null, destination, 0,
                status: "Skipped",
                error: $"{DestinationLabel(destination)} already has rows for {contno}.", ct);
            return new DataSyncResult(false,
                $"Allocation: {DestinationLabel(destination)} already has rows for {contno} — skipped.",
                skipId, 0);
        }

        // Source rows from LPMSIM — ALL approved batches for this ContNo. If a
        // container has both FillSKUMax + RoundRobin approved, both ship.
        List<SourceRow> sourceRows;
        int? primaryBatchNo;
        int totalAllocatedQty;
        await using (var src = OpenOnPremBackup())
        {
            sourceRows = (await src.QueryAsync<SourceRow>(new CommandDefinition(@"
                SELECT d.BatchNo,
                       d.ContNo, d.Country, d.TrnDate, d.Time1, d.UPC, d.Itemcode, d.Barcode,
                       d.GroupCode, d.POQty, d.SkuMax, d.AllocatedQty, d.PrevAllocatedQty, d.QtyIssue, d.Phase2Qty,
                       d.StoreID, d.TcmContno, d.Itemname, d.BuildingCategory, d.LPMDt, d.LPMBoxNO,
                       d.ORAPONo, d.Division, d.Brand, d.DivCode, d.Department, d.Season, d.Style,
                       [Size] = d.Size,
                       d.SalesPrice, d.ResultType, d.FinalResult, d.Result, d.Remarks, d.OTS,
                       d.PriorityRank, d.MnwToday,
                       Color    = u.color,
                       Gender   = u.GENDER,
                       HsCode   = u.hscode,
                       UsaGroupCode = u.GroupCode,
                       [Class]  = s.[Class],
                       Family   = s.Family,
                       Subclass = s.Subclass
                  FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
                  JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo
                  OUTER APPLY (
                       SELECT TOP 1 uo.color, uo.GENDER, uo.hscode, uo.GroupCode
                         FROM usa.dbo.usaorgfile uo WITH (NOLOCK)
                        WHERE uo.ContNo = d.ContNo AND uo.ItemCode = d.Itemcode
                        ORDER BY uo.TrnDate DESC
                  ) u
                  OUTER APPLY (
                       SELECT TOP 1 sm.[Class], sm.Family, sm.Subclass
                         FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                         LEFT JOIN datareporting.dbo.SubclassMaster sm WITH (NOLOCK) ON sm.MH4ID = v.MH4ID
                        WHERE v.itemcode = d.Itemcode
                  ) s
                 WHERE h.ContNo = @c AND h.ApprovedDt IS NOT NULL
                 ORDER BY h.BatchNo, d.IdNo",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

            primaryBatchNo    = sourceRows.FirstOrDefault()?.BatchNo;
            totalAllocatedQty = sourceRows.Sum(r => r.AllocatedQty ?? r.POQty ?? 0);
        }

        if (sourceRows.Count == 0)
            return new DataSyncResult(false, $"Allocation: no approved rows found for container {contno}.", null, 0);

        // Price gate for the legacy destination — BEFORE any row is written. Items at
        // printing stores must have a price; the pre-flight generates the missing ones
        // via stp_FindExportSalesPrice and reports whatever it could not resolve.
        if (destination == DataSyncDestination.WmsProductionDb)
        {
            var (missingPrices, generatedPrices) = await PreflightWmsProdPricesAsync(contno, ct);
            if (missingPrices.Count > 0)
            {
                var sample = string.Join("; ", missingPrices.Take(5)
                    .Select(m => $"{m.StoreID}/{m.Itemcode}" + (string.IsNullOrWhiteSpace(m.ResultMsg) ? "" : $" ({m.ResultMsg})")));
                var detail = $"{missingPrices.Count} item(s) at print-enabled stores still have no RFSalesPrice after " +
                             $"stp_FindExportSalesPrice: {sample}" + (missingPrices.Count > 5 ? " …" : "");
                var blockId = await WriteLogRowAsync(
                    contno, primaryBatchNo, destination, totalAllocatedQty,
                    status: "Failed", error: detail, ct);
                return new DataSyncResult(false,
                    $"Allocation to {DestinationLabel(destination)} BLOCKED — {detail}",
                    blockId, 0);
            }
            if (generatedPrices > 0)
                Console.Error.WriteLine($"[DataSync] {contno}: generated {generatedPrices} RFSalesPrice row(s) via stp_FindExportSalesPrice.");
        }

        string? error = null;
        int rowsCopied = 0;
        try
        {
            rowsCopied = destination switch
            {
                DataSyncDestination.AzureWmsDb       => await CopyToAzureWmsAsync(sourceRows, ct),
                DataSyncDestination.WmsProductionDb  => await CopyToWmsProductionDbAsync(sourceRows, ct),
                _ => throw new InvalidOperationException($"Unknown destination: {destination}"),
            };
        }
        catch (Exception ex) { error = ex.Message; }

        var syncId = await WriteLogRowAsync(
            contno, primaryBatchNo, destination, totalAllocatedQty,
            status: error is null ? "Success" : "Failed", error, ct);

        return error is null
            ? new DataSyncResult(true,
                $"Allocation: {rowsCopied:N0} rows copied to {DestinationLabel(destination)}.",
                syncId, rowsCopied)
            : new DataSyncResult(false,
                $"Allocation to {DestinationLabel(destination)} failed: {error}",
                syncId, rowsCopied);
    }

    // ----- pass 2: KNB boxes copy (independent gate) -----
    private async Task<DataSyncResult> TryCopyKnbBoxesAsync(string contno, CancellationToken ct)
    {
        if (await IsKnbBoxesPulledAsync(contno, ct))
        {
            var skipId = await WriteLogRowAsync(
                contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Skipped",
                error: "dbo.WmsKNBBoxes already has rows for this Country + ContNo.", ct);
            return new DataSyncResult(true,
                $"KNB boxes: skipped — Azure mirror already has rows for {contno}.",
                skipId, 0);
        }

        List<KnbBoxRow> rows;
        try
        {
            await using var src = OpenOnPremBackup();
            rows = (await src.QueryAsync<KnbBoxRow>(new CommandDefinition(
                @"SELECT palletno, Boxno, Contno, trndate, userid, closed, Remarks, whouse
                    FROM usa.dbo.KNBBoxes WITH (NOLOCK)
                   WHERE Contno = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Failed", error: $"Source read: {ex.Message}", ct);
            return new DataSyncResult(false, $"KNB boxes read failed: {ex.Message}", failId, 0);
        }

        if (rows.Count == 0)
        {
            var emptyId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Empty", error: $"usa.dbo.KNBBoxes returned no rows for Contno = {contno}.", ct);
            return new DataSyncResult(true, $"KNB boxes: source has no rows for {contno}.", emptyId, 0);
        }

        string? writeError = null;
        try
        {
            var dt = BuildKnbBoxDataTable(user.Country ?? "", rows);
            await using var conn = OpenWms();
            using var bulk = new SqlBulkCopy(conn)
            {
                DestinationTableName = "dbo.WmsKNBBoxes",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }
        catch (Exception ex) { writeError = ex.Message; }

        var logId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes,
            totalAllocatedQty: rows.Count,
            status: writeError is null ? "Success" : "Failed",
            error: writeError, ct);

        return writeError is null
            ? new DataSyncResult(true,  $"KNB boxes: {rows.Count:N0} row(s) pulled.", logId, rows.Count)
            : new DataSyncResult(false, $"KNB boxes write failed: {writeError}",       logId, 0);
    }

    // ----- destination writers -----

    private async Task<int> CopyToAzureWmsAsync(List<SourceRow> rows, CancellationToken ct)
    {
        // Mirror table — straight column-for-column SqlBulkCopy from SourceRow.
        var dt = BuildAzureMirrorDataTable(rows);
        await using var conn = OpenWms();
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.WMS_ContAllocationData",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
        return rows.Count;
    }

    private async Task<int> CopyToWmsProductionDbAsync(List<SourceRow> rows, CancellationToken ct)
    {
        // online.dbo.PhotoCheckingResult expects per-PIECE rows enriched from
        // bfldata.dbo.DataSettings, so this is not a straight column copy:
        //   * one row per piece (Qty always 1)
        //   * Result / flags / Company / ShopCode from the store's DataSettings row
        //   * OrPrice from the store's own DB, SalesPrice = FCCode + ' ' + OrPrice
        //   * RefNo from BFLDATA..RFIDTransfer, created for today if absent
        var settings = await LoadProdStoreSettingsAsync(rows.Select(r => r.StoreID), ct);
        var prices   = await LoadRfSalesPricesAsync(rows.Select(r => r.Itemcode), settings, ct);
        var refNos   = await ResolveRefNosAsync(settings, ct);

        var dt = BuildPhotoCheckingResultDataTable(rows, settings, prices, refNos);

        await using var conn = OpenWmsProductionDb();
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "online.dbo.PhotoCheckingResult",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);

        // Exploded row count, not the source row count — the caller reports it.
        return dt.Rows.Count;
    }

    // ----- WMS-Prod-DB price pre-flight -----

    /// <summary>
    /// @UserID for BFLData.dbo.stp_FindExportSalesPrice. WMS identifies users by
    /// username, not by the numeric id the legacy app passed via gUserInfo.UserID,
    /// so scheduled and UI runs alike stamp this service-account value.
    /// </summary>
    private const int SalesPriceUserId = 0;

    /// <summary>One (store, item) that still has no price after the generate attempt.</summary>
    public sealed record MissingPriceRow(string StoreID, string ShopName, string Itemcode, string? UPC, string? ResultMsg);

    /// <summary>
    /// Runs BEFORE any WMS-Prod-DB write. For every (store, item) whose store has
    /// POAllocation_PrintFlag = Y, checks [Dataname].dbo.RFSalesPrice for a price;
    /// where none exists, calls BFLData.dbo.stp_FindExportSalesPrice to generate one
    /// (the proc inserts into RFSalesPrice), then re-reads. Anything still unpriced
    /// is returned, and the caller aborts the sync rather than writing rows whose
    /// stickers would print without a price.
    ///
    /// Non-printing stores are ignored — they never get OrPrice/SalesPrice anyway.
    /// </summary>
    public async Task<(List<MissingPriceRow> Missing, int Generated)> PreflightWmsProdPricesAsync(
        string contno, CancellationToken ct = default)
    {
        var missing = new List<MissingPriceRow>();
        if (string.IsNullOrWhiteSpace(contno)) return (missing, 0);

        // Light projection of the approved rows — just what pricing needs.
        List<(string? StoreID, string? Itemcode, string? UPC)> pairs;
        await using (var src = OpenOnPremBackup())
        {
            pairs = (await src.QueryAsync<(string? StoreID, string? Itemcode, string? UPC)>(new CommandDefinition(@"
                SELECT DISTINCT d.StoreID, d.Itemcode, d.UPC
                  FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
                  JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo
                 WHERE h.ContNo = @c AND h.ApprovedDt IS NOT NULL",
                new { c = contno.Trim() },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        if (pairs.Count == 0) return (missing, 0);

        var settings = await LoadProdStoreSettingsAsync(pairs.Select(p => p.StoreID), ct);

        // Only stores that actually print stickers need a price.
        var needsPrice = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.StoreID) && !string.IsNullOrWhiteSpace(p.Itemcode))
            .Select(p => (Store: p.StoreID!.Trim(), Item: p.Itemcode!.Trim(), p.UPC))
            .Where(p => settings.TryGetValue(p.Store, out var st) && st.PrintFlagYes
                        && !string.IsNullOrWhiteSpace(st.Dataname))
            .ToList();
        if (needsPrice.Count == 0) return (missing, 0);

        var prices = await LoadRfSalesPricesAsync(needsPrice.Select(p => (string?)p.Item), settings, ct);

        // Generate the ones that are absent, one proc call per (shop, item).
        var generated = 0;
        var msgByKey  = new Dictionary<(string, string), string?>();
        var toGenerate = needsPrice
            .Where(p => !prices.ContainsKey((settings[p.Store].Dataname!.Trim(), p.Item)))
            .GroupBy(p => (Shop: settings[p.Store].ShopName?.Trim() ?? "", p.Item))
            .Where(g => g.Key.Shop.Length > 0)
            .ToList();

        if (toGenerate.Count > 0)
        {
            // The proc runs on the WmsProductionDb connection, NOT OnPremBackup: the
            // backup login has no EXECUTE right on BFLDATA.dbo.stp_FindExportSalesPrice
            // ("The EXECUTE permission was denied ... database 'BFLDATA', schema 'dbo'").
            //
            // The re-read below deliberately stays on OnPremBackup, because that is the
            // connection the actual write reads prices through — validating against a
            // different one could pass here and still write a blank price.
            await using var c = OpenWmsProductionDb();
            foreach (var g in toGenerate)
            {
                var sample = g.First();
                try
                {
                    var p = new DynamicParameters();
                    // NOTE: the legacy caller passed the UPC value to BOTH @UPC and
                    // @ItemCode. Replicated verbatim — this is the behaviour proven in
                    // production, and "fixing" it blind could price items wrongly.
                    p.Add("@UPC",       sample.UPC);
                    p.Add("@ItemCode",  sample.UPC);
                    p.Add("@ShopName",  g.Key.Shop);
                    p.Add("@UserID",    SalesPriceUserId);
                    p.Add("@ResultPrice", dbType: DbType.Double, direction: ParameterDirection.Output);
                    p.Add("@ResultMsg",   dbType: DbType.String, direction: ParameterDirection.Output, size: 250);

                    await c.ExecuteAsync(new CommandDefinition(
                        "BFLData.dbo.stp_FindExportSalesPrice", p,
                        commandType: CommandType.StoredProcedure,
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                    msgByKey[(g.Key.Shop, g.Key.Item)] = p.Get<string?>("@ResultMsg");
                    generated++;
                }
                catch (Exception ex)
                {
                    msgByKey[(g.Key.Shop, g.Key.Item)] = ex.Message;
                }
            }

            // Re-read: the proc inserts into RFSalesPrice, so prices should now exist.
            prices = await LoadRfSalesPricesAsync(needsPrice.Select(x => (string?)x.Item), settings, ct);
        }

        foreach (var p in needsPrice)
        {
            var st = settings[p.Store];
            if (prices.ContainsKey((st.Dataname!.Trim(), p.Item))) continue;
            var shop = st.ShopName?.Trim() ?? "";
            msgByKey.TryGetValue((shop, p.Item), out var msg);
            missing.Add(new MissingPriceRow(p.Store, shop, p.Item, p.UPC, msg));
        }

        return (missing, generated);
    }

    // ----- WMS-Prod-DB enrichment -----

    /// <summary>Per-store settings needed to build PhotoCheckingResult rows.</summary>
    private sealed class ProdStoreSettings
    {
        public string?  StoreID       { get; set; }
        public string?  ShopName      { get; set; }
        public string?  ShopLetter    { get; set; }
        public string?  ShopCode      { get; set; }
        public string?  Company       { get; set; }
        public string?  FCCode        { get; set; }
        public string?  Dataname      { get; set; }
        public string?  Decimals      { get; set; }
        public string?  POAllocationResult      { get; set; }
        public string?  POAllocation_PrintFlag  { get; set; }
        public string?  POAllocation_RFIDFlag   { get; set; }

        public bool PrintFlagYes => IsYes(POAllocation_PrintFlag);
        public bool RfidFlagYes  => IsYes(POAllocation_RFIDFlag);

        // Decimals drives the SalesPrice string; a Kuwait 3-decimal price rendered
        // as 2 would be wrong on the sticker. Falls back to 2 when unset/unparseable.
        public int DecimalPlaces =>
            int.TryParse(Decimals, out var d) && d >= 0 && d <= 6 ? d : 2;

        private static bool IsYes(string? s) =>
            !string.IsNullOrWhiteSpace(s) && s.Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One DataSettings row per StoreID present in the allocation. DataSettings can
    /// hold several rows for a StoreID, so one is picked deterministically by
    /// ShopName rather than left to query order.
    /// </summary>
    private async Task<Dictionary<string, ProdStoreSettings>> LoadProdStoreSettingsAsync(
        IEnumerable<string?> storeIdSource, CancellationToken ct)
    {
        var storeIds = storeIdSource.Where(s => !string.IsNullOrWhiteSpace(s))
                           .Select(s => s!.Trim())
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (storeIds.Length == 0) return new(StringComparer.OrdinalIgnoreCase);

        await using var c = OpenOnPremBackup();
        var list = (await c.QueryAsync<ProdStoreSettings>(new CommandDefinition(@"
            SELECT StoreID, ShopName, ShopLetter, ShopCode, Company, FCCode, Dataname, Decimals,
                   POAllocationResult, POAllocation_PrintFlag, POAllocation_RFIDFlag
              FROM (SELECT ds.*,
                           rn = ROW_NUMBER() OVER (PARTITION BY ds.StoreID ORDER BY ds.ShopName)
                      FROM bfldata.dbo.DataSettings ds WITH (NOLOCK)
                     WHERE ds.StoreID IN @ids) x
             WHERE rn = 1",
            new { ids = storeIds }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

        return list.Where(s => !string.IsNullOrWhiteSpace(s.StoreID))
                   .ToDictionary(s => s.StoreID!.Trim(), s => s, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Latest RFSalesPrice.Price per (Dataname, Itemcode) for the stores that print.
    /// One round-trip per distinct Dataname rather than per row. Trndate/Time1 make
    /// this a price history, so the most recent row wins.
    ///
    /// Only fetched for stores whose POAllocation_PrintFlag is Y — non-printing
    /// stores leave OrPrice and SalesPrice NULL.
    /// </summary>
    private async Task<Dictionary<(string Dataname, string Itemcode), decimal>> LoadRfSalesPricesAsync(
        IEnumerable<string?> itemcodeSource, Dictionary<string, ProdStoreSettings> settings, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), decimal>();

        var dataNames = settings.Values
            .Where(s => s.PrintFlagYes && !string.IsNullOrWhiteSpace(s.Dataname))
            .Select(s => s.Dataname!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => Regex.IsMatch(d, @"^[A-Za-z0-9_]+$"))   // 3-part name is interpolated
            .ToArray();
        if (dataNames.Length == 0) return result;

        var itemcodes = itemcodeSource.Where(i => !string.IsNullOrWhiteSpace(i))
                            .Select(i => i!.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (itemcodes.Length == 0) return result;
        var itemsCsv = string.Join(",", itemcodes);

        await using var c = OpenOnPremBackup();
        foreach (var dn in dataNames)
        {
            try
            {
                // CSV + STRING_SPLIT rather than IN @items: Dapper expands an IN list
                // to one parameter per item and SQL Server rejects a batch over 2100,
                // which a large container exceeds. Same pattern as
                // JafzaRoboProductionService and ManualAllocationService.
                var sql = $@"
                    SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ItemCode INTO #rfItems FROM STRING_SPLIT(@itemsCsv, ',');
                    CREATE CLUSTERED INDEX IX_rfItems ON #rfItems(ItemCode);

                    SELECT x.Itemcode, x.Price
                      FROM (SELECT p.Itemcode, p.Price,
                                   rn = ROW_NUMBER() OVER (PARTITION BY p.Itemcode
                                                           ORDER BY p.Trndate DESC, p.Time1 DESC)
                              FROM [{dn}].dbo.RFSalesPrice p WITH (NOLOCK)
                              INNER JOIN #rfItems i ON i.ItemCode = p.Itemcode) x
                     WHERE x.rn = 1;";
                var priced = await c.QueryAsync<(string Itemcode, decimal? Price)>(new CommandDefinition(
                    sql, new { itemsCsv },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                foreach (var p in priced)
                    if (p.Price.HasValue && !string.IsNullOrWhiteSpace(p.Itemcode))
                        result[((string)dn, p.Itemcode.Trim())] = p.Price.Value;
            }
            catch (Exception ex)
            {
                // A country DB without RFSalesPrice must not fail the whole sync —
                // those stores simply get no OrPrice. Surfaced in the log, not silent.
                Console.Error.WriteLine($"[DataSync] WARN: RFSalesPrice lookup failed for '{dn}': {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// One RFIDTransfer TrfNo per shop for today, created if it does not exist.
    /// Ported from the legacy getShopRefNo: TrfNo = ShopLetter + last digit of the
    /// year + a 3-digit per-shop-per-year sequence (e.g. O6224, E16168 — ShopLetter
    /// can be two characters).
    ///
    /// Differs from the VB original in two ways, both deliberate:
    ///   * The original tested `If Not Rs1 Is Nothing`, which is always true for a
    ///     DataSet, so the create branch was dead and Rows(0) threw on a shop with
    ///     no row for today. This implements the intent — reuse if present, else create.
    ///   * Read and insert run inside one transaction with UPDLOCK/HOLDLOCK, so two
    ///     concurrent syncs for the same shop cannot mint two TrfNos for one day.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveRefNosAsync(
        Dictionary<string, ProdStoreSettings> settings, CancellationToken ct)
    {
        var byShop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var shops = settings.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.ShopName))
            .GroupBy(s => s.ShopName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Shop: g.Key, Letter: g.Select(x => x.ShopLetter).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))))
            .ToList();
        if (shops.Count == 0) return byShop;

        var todayGst = DateTime.UtcNow.AddHours(4).Date;

        await using var c = OpenOnPremBackup();
        foreach (var (shop, letter) in shops)
        {
            try
            {
                var trfNo = await c.ExecuteScalarAsync<string?>(new CommandDefinition(@"
                    SET XACT_ABORT ON;
                    BEGIN TRAN;

                    DECLARE @trf varchar(15);

                    SELECT TOP 1 @trf = TrfNo
                      FROM BFLDATA.dbo.RFIDTransfer WITH (UPDLOCK, HOLDLOCK)
                     WHERE ShopName = @shop
                       AND CAST(TrfDate AS date) = @today;

                    IF @trf IS NULL
                    BEGIN
                        DECLARE @seq int =
                            ISNULL((SELECT MAX(CAST(RIGHT(TrfNo, 3) AS int))
                                      FROM BFLDATA.dbo.RFIDTransfer WITH (UPDLOCK, HOLDLOCK)
                                     WHERE ShopName = @shop
                                       AND YEAR(TrfDate) = YEAR(@today)
                                       AND RIGHT(TrfNo, 3) NOT LIKE '%[^0-9]%'), 0);

                        SET @trf = @letter
                                 + RIGHT(CAST(YEAR(@today) AS varchar(4)), 1)
                                 + RIGHT('000' + CAST(@seq + 1 AS varchar(10)), 3);

                        INSERT INTO BFLDATA.dbo.RFIDTransfer (ShopName, TrfNo, TrfDate)
                        VALUES (@shop, @trf, @today);
                    END

                    COMMIT;
                    SELECT @trf;",
                    new { shop, today = todayGst, letter = (letter ?? "").Trim() },
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                if (!string.IsNullOrWhiteSpace(trfNo)) byShop[shop] = trfNo!.Trim();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DataSync] WARN: RefNo resolve failed for shop '{shop}': {ex.Message}");
            }
        }
        return byShop;
    }

    private async Task<int?> WriteLogRowAsync(string contno, int? batchNo, DataSyncDestination dest,
        int? totalAllocatedQty, string status, string? error, CancellationToken ct,
        string origin = "Manual", string? actorOverride = null)
    {
        try
        {
            await using var c = OpenWms();
            return await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                INSERT INTO dbo.WMS_ContAllocationDataSync_Log
                    (ContNo, BatchNo, Destination, TotalAllocatedQty, Status, ErrorMessage, SyncedBy, Origin)
                OUTPUT INSERTED.SyncId
                VALUES (@c, @b, @d, @q, @s, @e, @u, @o)",
                new { c = contno, b = batchNo, d = dest.ToString(),
                      q = totalAllocatedQty, s = status, e = error,
                      u = actorOverride ?? user.Name, o = origin },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        catch
        {
            // The log INSERT shouldn't itself drop the sync result — swallow.
            return null;
        }
    }

    // ===================== DataTable builders =====================

    private static System.Data.DataTable BuildAzureMirrorDataTable(List<SourceRow> rows)
    {
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
        dt.Columns.Add("Phase2Qty",        typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("LPMBoxNO",         typeof(string));
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
        dt.Columns.Add("Result",           typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("OTS",              typeof(double));
        dt.Columns.Add("Color",            typeof(string));
        dt.Columns.Add("Gender",           typeof(string));
        dt.Columns.Add("HsCode",           typeof(string));
        dt.Columns.Add("Class",            typeof(string));
        dt.Columns.Add("Family",           typeof(string));
        dt.Columns.Add("Subclass",         typeof(string));
        dt.Columns.Add("PriorityRank",     typeof(int));
        dt.Columns.Add("MnwToday",         typeof(int));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                (object?)r.BatchNo          ?? DBNull.Value,
                (object?)r.ContNo           ?? DBNull.Value,
                (object?)r.Country          ?? DBNull.Value,
                (object?)r.TrnDate          ?? DBNull.Value,
                (object?)r.Time1            ?? DBNull.Value,
                (object?)r.UPC              ?? DBNull.Value,
                (object?)r.Itemcode         ?? DBNull.Value,
                (object?)r.Barcode          ?? DBNull.Value,
                (object?)r.GroupCode        ?? DBNull.Value,
                (object?)r.POQty            ?? DBNull.Value,
                (object?)r.SkuMax           ?? DBNull.Value,
                (object?)r.AllocatedQty     ?? DBNull.Value,
                (object?)r.PrevAllocatedQty ?? DBNull.Value,
                (object?)r.QtyIssue         ?? DBNull.Value,
                (object?)r.Phase2Qty        ?? DBNull.Value,
                (object?)r.StoreID          ?? DBNull.Value,
                (object?)r.TcmContno        ?? DBNull.Value,
                (object?)r.Itemname         ?? DBNull.Value,
                (object?)r.BuildingCategory ?? DBNull.Value,
                (object?)r.LPMDt            ?? DBNull.Value,
                (object?)r.LPMBoxNO         ?? DBNull.Value,
                (object?)r.ORAPONo          ?? DBNull.Value,
                (object?)r.Division         ?? DBNull.Value,
                (object?)r.Brand            ?? DBNull.Value,
                (object?)r.DivCode          ?? DBNull.Value,
                (object?)r.Department       ?? DBNull.Value,
                (object?)r.Season           ?? DBNull.Value,
                (object?)r.Style            ?? DBNull.Value,
                (object?)r.Size             ?? DBNull.Value,
                ParseDecimalOrDbNull(r.SalesPrice),
                (object?)r.ResultType       ?? DBNull.Value,
                (object?)r.FinalResult      ?? DBNull.Value,
                (object?)r.Result           ?? DBNull.Value,
                (object?)r.Remarks          ?? DBNull.Value,
                (object?)r.OTS              ?? DBNull.Value,
                (object?)r.Color            ?? DBNull.Value,
                (object?)r.Gender           ?? DBNull.Value,
                (object?)r.HsCode           ?? DBNull.Value,
                (object?)r.Class            ?? DBNull.Value,
                (object?)r.Family           ?? DBNull.Value,
                (object?)r.Subclass         ?? DBNull.Value,
                (object?)r.PriorityRank     ?? DBNull.Value,
                (object?)r.MnwToday         ?? DBNull.Value);
        }
        return dt;
    }

    private static System.Data.DataTable BuildPhotoCheckingResultDataTable(
        List<SourceRow> rows,
        Dictionary<string, ProdStoreSettings> settings,
        Dictionary<(string Dataname, string Itemcode), decimal> prices,
        Dictionary<string, string> refNos)
    {
        // One row PER PIECE: an allocation of 8 becomes 8 rows with Qty = 1, because
        // the legacy checking flow scans individual pieces. Rows with no allocated
        // qty produce nothing.
        //
        // Still not populated (no source): QtyIssuedResult, PStatus, PDateTime.
        // RDateTime is intentionally left off the DataTable so it lands NULL.
        var dt = new System.Data.DataTable();
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Result",           typeof(string));   // DataSettings.POAllocationResult
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("OrPrice",          typeof(double));   // float on PhotoCheckingResult
        dt.Columns.Add("PrintFlag",        typeof(string));
        dt.Columns.Add("RfidFlag",         typeof(string));
        dt.Columns.Add("Company",          typeof(string));
        dt.Columns.Add("ShopCode",         typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(string));   // varchar(30) on PhotoCheckingResult
        dt.Columns.Add("RefNo",            typeof(string));
        dt.Columns.Add("Mark",             typeof(string));
        dt.Columns.Add("Uid",              typeof(string));   // varchar(5), not numeric
        dt.Columns.Add("RStatus",          typeof(string));
        dt.Columns.Add("Excess",           typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("LPMBoxNO",         typeof(string));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("StoreId",          typeof(string));

        foreach (var r in rows)
        {
            var pieces = r.AllocatedQty ?? r.POQty ?? 0;
            if (pieces <= 0) continue;

            var storeId = r.StoreID?.Trim() ?? "";
            settings.TryGetValue(storeId, out var st);

            var printY = st?.PrintFlagYes == true;

            // OrPrice / SalesPrice only when the store prints stickers. OrPrice
            // defaults to 0 (not NULL) when the store doesn't print or has no price.
            double orPrice    = 0.0;
            object salesPrice = "";
            if (printY && st is not null)
            {
                var dn = st.Dataname?.Trim();
                var ic = r.Itemcode?.Trim();
                if (!string.IsNullOrEmpty(dn) && !string.IsNullOrEmpty(ic)
                    && prices.TryGetValue((dn, ic), out var p))
                {
                    orPrice = (double)p;
                    // FCCode + space + price at the store's configured decimals.
                    salesPrice = ((st.FCCode ?? "").Trim() + " " +
                                  p.ToString("F" + st.DecimalPlaces,
                                             System.Globalization.CultureInfo.InvariantCulture)).Trim();
                }
            }

            var refNo = st?.ShopName is { Length: > 0 } shop && refNos.TryGetValue(shop.Trim(), out var rn)
                ? (object)rn
                : "";

            // Barcode column carries the composite "Barcode/OrPrice/RefNo" text
            // rather than the raw scanned barcode.
            var refNoText = refNo as string ?? "";
            var barcode = $"{r.Barcode}/{orPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{refNoText}";

            for (var i = 0; i < pieces; i++)
            {
                dt.Rows.Add(
                    (object?)r.ContNo           ?? DBNull.Value,
                    (object?)r.TrnDate          ?? DBNull.Value,
                    (object?)r.Time1            ?? DBNull.Value,
                    (object?)r.UPC              ?? DBNull.Value,
                    (object?)r.Itemcode         ?? DBNull.Value,
                    // GroupCode comes from usa.dbo.usaorgfile for this (ContNo, ItemCode).
                    // Falls back to the allocation's own value when the PO line has none,
                    // rather than writing NULL and losing what we already had.
                    (object?)(r.UsaGroupCode ?? r.GroupCode) ?? DBNull.Value,
                    (object?)r.Season           ?? DBNull.Value,
                    (object?)r.Department       ?? DBNull.Value,
                    (object?)r.Division         ?? DBNull.Value,
                    (object?)st?.POAllocationResult ?? DBNull.Value,
                    (object?)r.FinalResult      ?? DBNull.Value,
                    (object?)r.ResultType       ?? DBNull.Value,
                    1,                                   // one piece per row
                    (object?)r.QtyIssue         ?? DBNull.Value,
                    orPrice,
                    printY ? "Y" : "N",
                    st?.RfidFlagYes == true ? "Y" : "N",
                    st?.Company ?? "",
                    (object?)st?.ShopCode       ?? DBNull.Value,
                    (object?)r.Itemname         ?? DBNull.Value,
                    barcode,
                    salesPrice,
                    refNo,
                    "PA",                                // Mark
                    "0",                                 // Uid (varchar)
                    "N",                                 // RStatus
                    "N",                                 // Excess
                    (object?)r.TcmContno        ?? DBNull.Value,
                    (object?)r.BuildingCategory ?? DBNull.Value,
                    (object?)r.LPMDt            ?? DBNull.Value,
                    (object?)r.LPMBoxNO         ?? DBNull.Value,
                    (object?)r.ORAPONo          ?? DBNull.Value,
                    (object?)r.Style            ?? DBNull.Value,
                    (object?)r.Remarks          ?? DBNull.Value,
                    (object?)r.StoreID          ?? DBNull.Value);
            }
        }
        return dt;
    }

    private static object ParseDecimalOrDbNull(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : (object)DBNull.Value;

    private static string DestinationLabel(DataSyncDestination d) => d switch
    {
        DataSyncDestination.AzureWmsDb       => "Azure WMS DB",
        DataSyncDestination.WmsProductionDb  => "WMS-Prod-DB",
        DataSyncDestination.WmsKnbBoxes      => "Azure WMS — KNB Boxes",
        DataSyncDestination.WmsProdDbToAzure => "WMSPRODDB → Azure WMS",
        _                                    => d.ToString(),
    };

    private static System.Data.DataTable BuildKnbBoxDataTable(string country, List<KnbBoxRow> rows)
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("Country",  typeof(string));
        dt.Columns.Add("palletno", typeof(string));
        dt.Columns.Add("Boxno",    typeof(string));
        dt.Columns.Add("Contno",   typeof(string));
        dt.Columns.Add("trndate",  typeof(DateTime));
        dt.Columns.Add("userid",   typeof(string));
        dt.Columns.Add("closed",   typeof(string));
        dt.Columns.Add("Remarks",  typeof(string));
        dt.Columns.Add("whouse",   typeof(string));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                country ?? "",
                (object?)r.palletno ?? DBNull.Value,
                r.Boxno  ?? "",                       // PK component — must be non-null
                r.Contno ?? "",                       // PK component — must be non-null
                (object?)r.trndate  ?? DBNull.Value,
                (object?)r.userid   ?? DBNull.Value,
                (object?)r.closed   ?? DBNull.Value,
                (object?)r.Remarks  ?? DBNull.Value,
                (object?)r.whouse   ?? DBNull.Value);
        }
        return dt;
    }

    // Materialised from usa.dbo.KNBBoxes — class (not record) so Dapper does
    // per-column type coercion, matching the SourceRow pattern.
    private sealed class KnbBoxRow
    {
        public string?   palletno { get; set; }
        public string?   Boxno    { get; set; }
        public string?   Contno   { get; set; }
        public DateTime? trndate  { get; set; }
        public string?   userid   { get; set; }
        public string?   closed   { get; set; }
        public string?   Remarks  { get; set; }
        public string?   whouse   { get; set; }
    }

    // Mirror the columns we read out of LPMSIM. A class with settable
    // properties (not a positional record) — Dapper's record-constructor
    // matching is strict on parameter type, but property-based hydration
    // does per-column type coercion (string -> decimal etc.), which we
    // need because LPMSIM's SalesPrice is varchar while the Azure mirror
    // column is decimal.
    private sealed class SourceRow
    {
        public int?      BatchNo          { get; set; }
        public string?   ContNo           { get; set; }
        public string?   Country          { get; set; }
        public DateTime? TrnDate          { get; set; }
        public TimeSpan? Time1            { get; set; }
        public string?   UPC              { get; set; }
        public string?   Itemcode         { get; set; }
        public string?   Barcode          { get; set; }
        public string?   GroupCode        { get; set; }
        // usa.dbo.usaorgfile.GroupCode for this (ContNo, ItemCode). Kept separate from
        // the LPMSIM GroupCode above so only the WMS-Prod-DB write switches source —
        // the Azure mirror keeps copying the allocation's own value.
        public string?   UsaGroupCode     { get; set; }
        public int?      POQty            { get; set; }
        public int?      SkuMax           { get; set; }
        public int?      AllocatedQty     { get; set; }
        public int?      PrevAllocatedQty { get; set; }
        public int?      QtyIssue         { get; set; }
        public int?      Phase2Qty        { get; set; }
        public string?   StoreID          { get; set; }
        public string?   TcmContno        { get; set; }
        public string?   Itemname         { get; set; }
        public string?   BuildingCategory { get; set; }
        public DateTime? LPMDt            { get; set; }
        public string?   LPMBoxNO         { get; set; }
        public string?   ORAPONo          { get; set; }
        public string?   Division         { get; set; }
        public string?   Brand            { get; set; }
        public int?      DivCode          { get; set; }
        public string?   Department       { get; set; }
        public string?   Season           { get; set; }
        public string?   Style            { get; set; }
        public string?   Size             { get; set; }
        public string?   SalesPrice       { get; set; }  // varchar on LPMSIM; parsed to decimal for the Azure mirror
        public string?   ResultType       { get; set; }
        public string?   FinalResult      { get; set; }
        public string?   Result           { get; set; }
        public string?   Remarks          { get; set; }
        public double?   OTS              { get; set; }
        public string?   Color            { get; set; }
        public string?   Gender           { get; set; }
        public string?   HsCode           { get; set; }
        public string?   Class            { get; set; }
        public string?   Family           { get; set; }
        public string?   Subclass         { get; set; }
        public int?      PriorityRank     { get; set; }
        public int?      MnwToday         { get; set; }
    }

    // ===================== Data Settings sync (standalone) =====================

    /// <summary>The 138 mirror columns of bfldata.dbo.DataSettings, in source
    /// order. Drives both the SELECT and the per-column SqlBulkCopy mapping so
    /// the two stay aligned.</summary>
    private static readonly string[] DataSettingsColumns = new[]
    {
        "ShopName","Dataname","UnitCode","FCCode","FCRate","CeilingType",
        "CostCodeFrom","LocCodeFrom","CostCodeTo","LocCodeTo","Decimals",
        "TCMItemCode","USAItemCode","Transfer","TargetServer","TargetDatabase",
        "CostCode","Import","TargetPath","CalcInv","AttendancePath","ExportData",
        "BranchCode","Form69F","barshopname","barcompname","DailyQuota","RepRowNo",
        "USA","Add1","Add2","Add3","Add4","Add5","Add6","MaxQtyField","Itemdisc",
        "TCMTarget","ShopLetter","CurrStock","SalesQty","TCMHTarget","TCMCTarget",
        "TCMWTarget","PRCreditCode","Transport","DueToFromAc","NewTCMPrice","MaxQty",
        "TrfQty","StopDel","TCMStock","OpenDate","RFId","RFTag","Area","Size",
        "Active","OracleLocation","MaxQtyW","CurrStockW","TrfQtyW","MaxQtyH",
        "CurrStockH","TrfQtyH","AmzDb","PalletPrefix","Production","PalletType",
        "RetailNext","StoreID","ERPCostcode","ShopSizeSQFt","EmaarStore",
        "Emaar_TenantCode","DefaultMinQty","ShopEmail","OnlineCountryId","erploccode",
        "Company","MUYShop","CollectionSize","Remarks","DraftORPercMax",
        "ExportActive","MixMaxFLAG","GRPMIXFLAG","CollectionDay","ShopInShop",
        "R1ToGo","AnyP","MuyStoreID","IAQtyField","IATrfQtyField","AddSalesPricePerc",
        "R1Prod","ShopGrade","shift","SalesIntegrated","PrintWasNow","CountryCode",
        "AttendancePort","POS","BANK","Country","CalcVat","ExportWH",
        "ExportCountryCode","ERPLedgerID","SizeSqMtTotal","SizeTCMSqMt","ExportP2",
        "ProductionRWH","PalletTypeW","SalesIntegration","RouteId","ISOCountryCode",
        "VATPerc","ShopCode","ShopSupervisor","bckbarshopname","TelNo",
        "ProdActiveFromJafza","GradeLetter","ShopType","RoboShopId","spcode",
        "ActiveStore","RMSStoreID","CoffeeShopLetter","OnlinePriceAPI","ExpDataName",
        "ExpCostCode","PrintFcCode","PrintPriceSticker","ExpLocCode","PBFullname",
        "CalcVatForOnlineReturn","ExpInterCompAc","Concept","CloseDate","GcpOpenDate",
        "CreateDate","MFCSSOH","CountryID","SIMCountry"
    };

    /// <summary>Get the high-water mark used by the incremental sync. Returns
    /// NULL if the Azure mirror is empty (first-run full pull).</summary>
    public async Task<DateTime?> GetDataSettingsLastSyncedCreateDateAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(CreateDate) FROM dbo.WMS_DataSettings WITH (NOLOCK)",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Pull bfldata.dbo.DataSettings rows with CreateDate &gt; the Azure
    /// high-water mark (or all rows on first run) and SqlBulkCopy them into
    /// dbo.WMS_DataSettings. Logs one row to WMS_ContAllocationDataSync_Log
    /// with ContNo='(DataSettings)' and Destination='WMSDataSettings'.</summary>
    public async Task<DataSyncResult> SyncDataSettingsAsync(CancellationToken ct = default)
    {
        DateTime? lastMax;
        try
        {
            lastMax = await GetDataSettingsLastSyncedCreateDateAsync(ct);
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync("(DataSettings)", null, DataSyncDestination.WMSDataSettings, 0,
                "Failed", $"Reading high-water mark on Azure failed: {ex.Message}", ct);
            return new DataSyncResult(false, $"Data Settings: cannot read MAX(CreateDate) on Azure ({ex.Message}).", failId, 0);
        }

        var cols    = string.Join(", ", DataSettingsColumns.Select(c => $"[{c}]"));
        var where   = lastMax.HasValue ? "WHERE CreateDate > @lastMax" : "";
        var orderBy = "ORDER BY CreateDate";
        var sql     = $"SELECT {cols} FROM bfldata.dbo.DataSettings WITH (NOLOCK) {where} {orderBy}";

        int rowsCopied = 0;
        string? error = null;
        try
        {
            await using var src = OpenOnPremBackup();
            using var cmd = new SqlCommand(sql, src) { CommandTimeout = CommandTimeoutSeconds };
            if (lastMax.HasValue) cmd.Parameters.Add(new SqlParameter("@lastMax", System.Data.SqlDbType.DateTime) { Value = lastMax.Value });
            using var reader = await cmd.ExecuteReaderAsync(ct);

            await using var dest = OpenWms();
            using var bulk = new SqlBulkCopy(dest)
            {
                DestinationTableName = "dbo.WMS_DataSettings",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
                EnableStreaming      = true,
            };
            foreach (var col in DataSettingsColumns)
                bulk.ColumnMappings.Add(col, col);

            await bulk.WriteToServerAsync(reader, ct);
            rowsCopied = bulk.RowsCopied;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var syncId = await WriteLogRowAsync("(DataSettings)", null, DataSyncDestination.WMSDataSettings,
            totalAllocatedQty: rowsCopied,
            status: error is null ? "Success" : "Failed",
            error: error, ct);

        if (error is not null)
            return new DataSyncResult(false, $"Data Settings sync failed: {error}", syncId, 0);

        var sinceText = lastMax.HasValue
            ? $" (since {lastMax.Value:yyyy-MM-dd HH:mm})"
            : " (full mirror — first run)";
        return new DataSyncResult(true,
            $"Data Settings: {rowsCopied:N0} row(s) synced{sinceText}.",
            syncId, rowsCopied);
    }

    // ===================== ToteID Master sync (per-country) =====================

    private static readonly System.Text.RegularExpressions.Regex SafeDbName =
        new(@"^[A-Za-z0-9_]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Run the ToteID Master sync across every country present in
    /// dbo.WMS_DataSettings. Each country gets its own log row in
    /// WMS_ContAllocationDataSync_Log (Destination='ToteIDMaster',
    /// ContNo=&lt;country code&gt;), so Recent Activity shows per-country status
    /// separately. UAE is special-cased (source = bfldata.dbo.BlueToteIDMaster,
    /// used-flag source = racks.dbo.whboxitems). Other countries read from
    /// {DataName}.dbo.BlueToteIDMaster and {DataName}.dbo.WHboxitemsexport
    /// via OnPremBackup using 3-part names.</summary>
    public async Task<List<CountryToteSyncRow>> SyncToteIDMasterAsync(
        string origin = "Manual", string? actor = null, CancellationToken ct = default)
    {
        // 1. Country list from the Azure DataSettings mirror.
        List<(string SIMCountry, string Dataname)> countries;
        try
        {
            await using var w = OpenWms();
            countries = (await w.QueryAsync<(string, string)>(new CommandDefinition(@"
                SELECT DISTINCT SIMCountry, Dataname
                  FROM dbo.WMS_DataSettings WITH (NOLOCK)
                 WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
                   AND Dataname   IS NOT NULL AND LTRIM(RTRIM(Dataname))   <> ''
                 ORDER BY SIMCountry",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            await WriteLogRowAsync("(ToteMaster)", null, DataSyncDestination.ToteIDMaster, 0,
                "Failed", $"Reading country list from WMS_DataSettings failed: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new List<CountryToteSyncRow> {
                new("(all)", "", 0, 0, "Failed",
                    $"Cannot read country list from dbo.WMS_DataSettings — run Phase 1 sync first. ({ex.Message})")
            };
        }

        if (countries.Count == 0)
        {
            await WriteLogRowAsync("(ToteMaster)", null, DataSyncDestination.ToteIDMaster, 0,
                "Empty", "dbo.WMS_DataSettings has no SIMCountry/Dataname rows yet.", ct,
                origin: origin, actorOverride: actor);
            return new List<CountryToteSyncRow> {
                new("(all)", "", 0, 0, "Empty",
                    "dbo.WMS_DataSettings has no SIMCountry rows. Run the Data Settings Sync first.")
            };
        }

        var results = new List<CountryToteSyncRow>();

        // UAE: process ONCE regardless of how many UAE DataNames are in WMS_DataSettings.
        // Source: bfldata.dbo.BlueToteIDMaster on OnPremBackup. Used flag: racks.dbo.whboxitems.
        if (countries.Any(r => string.Equals(r.SIMCountry, "UAE", StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(await SyncOneCountryAsync(
                country:        "UAE",
                sourceLabel:    "bfldata",
                toteSrcTable:   "bfldata.dbo.BlueToteIDMaster",
                usedSrcTable:   "racks.dbo.whboxitems",
                openSourceConn: () => OpenOnPremBackup(),
                origin:         origin,
                actor:          actor,
                ct: ct));
        }

        // Non-UAE: one iteration per (SIMCountry, Dataname). Each country uses its OWN
        // connection string ({Country}_DB_ConnectionString) — the source 3-part name
        // resolves on that country's server, not OnPremBackup.
        foreach (var (country, dataName) in countries
                     .Where(r => !string.Equals(r.SIMCountry, "UAE", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SafeDbName.IsMatch(dataName))
            {
                await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, 0,
                    "Failed", $"DataName '{dataName}' contains characters outside [A-Za-z0-9_].", ct,
                    origin: origin, actorOverride: actor);
                results.Add(new(country, dataName, 0, 0, "Failed", "DataName format invalid."));
                continue;
            }

            // If {Country}_DB_ConnectionString isn't configured (ECOM, Ex2Locations,
            // OMAN today), skip cleanly rather than blowing up on Invalid object name.
            try { _ = resolver.GetCountryConnectionString(country); }
            catch (InvalidOperationException)
            {
                var msg = $"Skipped: no {country}_DB_ConnectionString configured in App Service.";
                await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, 0,
                    "Skipped", msg, ct, origin: origin, actorOverride: actor);
                results.Add(new(country, dataName, 0, 0, "Skipped", msg));
                continue;
            }

            results.Add(await SyncOneCountryAsync(
                country:        country,
                sourceLabel:    dataName,
                toteSrcTable:   $"{dataName}.dbo.BlueToteIDMaster",
                usedSrcTable:   $"{dataName}.dbo.WHboxitemsexport",
                openSourceConn: () => OpenCountry(country),
                origin:         origin,
                actor:          actor,
                ct: ct));
        }

        return results;
    }

    /// <summary>Shared per-country sync body. Reads yesterday's totes from
    /// `toteSrcTable` via `openSourceConn`, dedups vs Azure, bulk-inserts new
    /// rows (Used='N', Country=country), then reads `usedSrcTable` via the same
    /// source connection and marks matching Azure rows Used='Y'.</summary>
    private async Task<CountryToteSyncRow> SyncOneCountryAsync(
        string country, string sourceLabel,
        string toteSrcTable, string usedSrcTable,
        Func<SqlConnection> openSourceConn,
        string origin, string? actor,
        CancellationToken ct)
    {
        // 1. Read yesterday's totes from source.
        List<string> sourceTotes;
        try
        {
            await using var src = openSourceConn();
            var sql = $@"SELECT DISTINCT ToteID
                           FROM {toteSrcTable} WITH (NOLOCK)
                          WHERE CurrDate >= DATEADD(day, -1, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE))
                            AND CurrDate <  CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE)
                            AND ToteID IS NOT NULL AND LTRIM(RTRIM(ToteID)) <> ''";
            sourceTotes = (await src.QueryAsync<string>(new CommandDefinition(
                sql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, 0,
                "Failed", $"Reading {toteSrcTable} failed: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new(country, sourceLabel, 0, 0, "Failed", $"Source read ({toteSrcTable}): {ex.Message}");
        }

        // 2. Filter out totes already present on Azure for this country (PK = Country+ToteID).
        HashSet<string> existing;
        try
        {
            await using var w = OpenWms();
            existing = (await w.QueryAsync<string>(new CommandDefinition(
                "SELECT ToteID FROM dbo.WmsBlueToteIDMaster WITH (NOLOCK) WHERE Country = @ct",
                new { ct = country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, 0,
                "Failed", $"Reading existing Azure ToteIDs failed: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new(country, sourceLabel, 0, 0, "Failed", $"Azure dedup read: {ex.Message}");
        }

        var newTotes = sourceTotes.Where(t => !existing.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int inserted = 0;

        // 3. Bulk-insert the new ones (Used = 'N', Country = SIMCountry).
        if (newTotes.Count > 0)
        {
            try
            {
                var dt = new System.Data.DataTable();
                dt.Columns.Add("Country",  typeof(string));
                dt.Columns.Add("ToteID",   typeof(string));
                dt.Columns.Add("CurrDate", typeof(DateTime));
                dt.Columns.Add("Remarks",  typeof(string));
                dt.Columns.Add("Used",     typeof(string));
                var today = DateTime.UtcNow.AddHours(4).Date.AddDays(-1);  // yesterday (GST) — when SIM created these
                foreach (var t in newTotes)
                    dt.Rows.Add(country, t, today, DBNull.Value, "N");

                await using var w = OpenWms();
                using var bulk = new SqlBulkCopy(w)
                {
                    DestinationTableName = "dbo.WmsBlueToteIDMaster",
                    BatchSize            = 1000,
                    BulkCopyTimeout      = CommandTimeoutSeconds,
                };
                foreach (System.Data.DataColumn col in dt.Columns)
                    bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                await bulk.WriteToServerAsync(dt, ct);
                inserted = newTotes.Count;
            }
            catch (Exception ex)
            {
                await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, 0,
                    "Failed", $"Insert into WmsBlueToteIDMaster failed: {ex.Message}", ct,
                    origin: origin, actorOverride: actor);
                return new(country, sourceLabel, 0, 0, "Failed", $"Bulk insert: {ex.Message}");
            }
        }

        // 4. Mark Used='Y' for any tote in this country whose ToteID is currently
        //    held in the used-source table (UAE: racks; others: WHboxitemsexport).
        //    Apply to ALL country rows so older inserts get updated too.
        int markedUsed = 0;
        try
        {
            await using var src = openSourceConn();
            var usedSql = $@"SELECT DISTINCT ToteId
                               FROM {usedSrcTable} WITH (NOLOCK)
                              WHERE ToteId IS NOT NULL AND LTRIM(RTRIM(ToteId)) <> ''";
            var inUseTotes = (await src.QueryAsync<string>(new CommandDefinition(
                usedSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

            if (inUseTotes.Count > 0)
            {
                await using var w = OpenWms();
                // Chunk to avoid hitting the 2100 sqlparameter limit on IN @list.
                const int chunkSize = 1000;
                for (int i = 0; i < inUseTotes.Count; i += chunkSize)
                {
                    var chunk = inUseTotes.Skip(i).Take(chunkSize).ToArray();
                    var n = await w.ExecuteAsync(new CommandDefinition(@"
                        UPDATE dbo.WmsBlueToteIDMaster
                           SET Used = 'Y'
                         WHERE Country = @ct
                           AND ToteID IN @list
                           AND (Used IS NULL OR Used = 'N')",
                        new { ct = country, list = chunk },
                        commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    markedUsed += n;
                }
            }
        }
        catch (Exception ex)
        {
            // The insert may have succeeded — log a "partial" outcome.
            await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, inserted,
                "PartialFailed",
                $"Inserted {inserted}; Used='Y' update failed reading {usedSrcTable}: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new(country, sourceLabel, inserted, 0, "PartialFailed",
                $"Inserted {inserted}; Used update failed ({usedSrcTable}): {ex.Message}");
        }

        var status = inserted == 0 && markedUsed == 0 ? "Empty" : "Success";
        var note   = sourceTotes.Count == 0
            ? $"Source has no yesterday rows in {toteSrcTable}."
            : $"Source returned {sourceTotes.Count} tote(s); {inserted} inserted, {markedUsed} marked Used='Y'.";
        await WriteLogRowAsync(country, null, DataSyncDestination.ToteIDMaster, inserted,
            status, note, ct, origin: origin, actorOverride: actor);
        return new(country, sourceLabel, inserted, markedUsed, status, note);
    }

    // ===================== WMSPROD used-totes flip =====================

    /// <summary>Pulls DISTINCT ToteID from on-prem `usa.dbo.upcboxhead`
    /// (via OnPremBackup) where `Closed = 'N'` (still-open boxes) and flips
    /// dbo.WmsBlueToteIDMaster.Used = 'Y' for any matching ToteIDs on Azure
    /// (any country). Logs one row with Destination='WmsProdUsedTotes'.
    /// Chunked to 1000-tote batches to stay under SQL Server's 2100 sqlparameter cap.
    /// </summary>
    public async Task<DataSyncResult> SyncWmsProdUsedTotesAsync(
        string origin = "Manual", string? actor = null, CancellationToken ct = default)
    {
        var dest = DataSyncDestination.WmsProdUsedTotes;

        // 1. Read distinct in-use ToteIDs from usa.dbo.upcboxhead (OnPremBackup).
        List<string> inUseTotes;
        try
        {
            await using var src = OpenOnPremBackup();
            inUseTotes = (await src.QueryAsync<string>(new CommandDefinition(
                @"SELECT DISTINCT ToteID
                    FROM usa.dbo.upcboxhead WITH (NOLOCK)
                   WHERE ISNULL(Closed, 'N') = 'N'
                     AND ToteID IS NOT NULL
                     AND LTRIM(RTRIM(ToteID)) <> ''",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync("(WmsProdUsedTotes)", null, dest, 0,
                "Failed", $"Reading usa.dbo.upcboxhead on OnPremBackup failed: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new DataSyncResult(false, $"WMSPROD used-totes read failed: {ex.Message}", failId, 0);
        }

        if (inUseTotes.Count == 0)
        {
            var emptyId = await WriteLogRowAsync("(WmsProdUsedTotes)", null, dest, 0,
                "Empty", "usa.dbo.upcboxhead returned no open (Closed='N') rows with ToteID.", ct,
                origin: origin, actorOverride: actor);
            return new DataSyncResult(true, "usa.dbo.upcboxhead has no open rows with ToteID.", emptyId, 0);
        }

        // 2. Flip Used='Y' on Azure. Country-wide match (any country).
        int marked = 0;
        string? writeError = null;
        try
        {
            await using var w = OpenWms();
            const int chunkSize = 1000;
            for (int i = 0; i < inUseTotes.Count; i += chunkSize)
            {
                var chunk = inUseTotes.Skip(i).Take(chunkSize).ToArray();
                var n = await w.ExecuteAsync(new CommandDefinition(@"
                    UPDATE dbo.WmsBlueToteIDMaster
                       SET Used = 'Y'
                     WHERE ToteID IN @list
                       AND (Used IS NULL OR Used = 'N')",
                    new { list = chunk }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                marked += n;
            }
        }
        catch (Exception ex) { writeError = ex.Message; }

        var logId = await WriteLogRowAsync("(WmsProdUsedTotes)", null, dest, marked,
            status: writeError is null ? "Success" : "Failed",
            error: writeError, ct, origin: origin, actorOverride: actor);

        return writeError is null
            ? new DataSyncResult(true,
                $"WMSPROD used-totes: {inUseTotes.Count:N0} distinct ToteID(s) read; {marked:N0} row(s) flipped to Used='Y' on Azure.",
                logId, marked)
            : new DataSyncResult(false,
                $"WMSPROD used-totes update failed: {writeError}",
                logId, 0);
    }

    // ===================== Boxes push to WMSPROD =====================

    /// <summary>
    /// Pushes Azure dbo.WmsUPCBoxHead + WmsUPCBoxDet rows to on-prem
    /// usa.dbo.upcboxhead + usa.dbo.upcboxdet incrementally, filtering on
    /// WmsUPCBoxHead.PublishedTS IS NULL (i.e. never published). Dedups by
    /// BoxNo — if the row already exists on the target, it's skipped but
    /// PublishedTS is still stamped so we don't reprocess it next run.
    ///
    /// Head + Det rows for each box are written in a single on-prem
    /// transaction. If Det rows fail after Head succeeded, the transaction
    /// rolls back and the Azure PublishedTS is left NULL for retry.
    ///
    /// Logs one summary row per run with Destination='BoxesToWmsProd'.
    /// </summary>
    public async Task<DataSyncResult> SyncBoxesToWmsProdAsync(
        string origin = "Manual", string? actor = null, CancellationToken ct = default)
    {
        var dest = DataSyncDestination.BoxesToWmsProd;

        // 1. Pull all unpublished Head rows + their Det rows from Azure.
        List<PushHeadRow> heads;
        Dictionary<string, List<PushDetRow>> detsByBox;
        try
        {
            await using var w = OpenWms();
            heads = (await w.QueryAsync<PushHeadRow>(new CommandDefinition(@"
                SELECT Country, BoxNo, TrnDate, Time1, PreparedBy, PalletType, ToteID, LPMDT, PONo,
                       WHouse, Userid, Closed, Remarks
                  FROM dbo.WmsUPCBoxHead WITH (NOLOCK)
                 WHERE PublishedTS IS NULL
                 ORDER BY TrnDate, BoxNo",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

            if (heads.Count == 0)
            {
                var emptyId = await WriteLogRowAsync("(BoxesToWmsProd)", null, dest, 0,
                    "Empty", "No unpublished WmsUPCBoxHead rows to push.", ct,
                    origin: origin, actorOverride: actor);
                return new DataSyncResult(true, "No unpublished boxes to push.", emptyId, 0);
            }

            var boxNos = heads.Select(h => h.BoxNo).ToArray();
            var detRows = (await w.QueryAsync<PushDetRow>(new CommandDefinition(@"
                SELECT Country, BoxNo, Itemcode, SrNo, Qty, UPC, StoreId, Status, ToteID
                  FROM dbo.WmsUPCBoxDet WITH (NOLOCK)
                 WHERE BoxNo IN @b",
                new { b = boxNos },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

            detsByBox = detRows.GroupBy(d => d.BoxNo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync("(BoxesToWmsProd)", null, dest, 0,
                "Failed", $"Reading unpublished rows from Azure failed: {ex.Message}", ct,
                origin: origin, actorOverride: actor);
            return new DataSyncResult(false, $"Boxes push read failed: {ex.Message}", failId, 0);
        }

        // 2. For each Head, check existence on target and insert if new.
        int pushed = 0, skipped = 0, failed = 0;
        var errors = new List<string>();

        foreach (var h in heads)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await using var opb = OpenOnPremBackup();
                await using var tx = (SqlTransaction)await opb.BeginTransactionAsync(ct);

                var exists = await opb.ExecuteScalarAsync<int?>(new CommandDefinition(
                    @"SELECT TOP 1 1 FROM usa.dbo.upcboxhead WITH (NOLOCK) WHERE BoxNo = @b",
                    new { b = h.BoxNo }, transaction: tx, cancellationToken: ct));

                if (exists == 1)
                {
                    // Already on target — no insert, but stamp Azure PublishedTS
                    // so we don't reprocess. Skip status counts as success.
                    await tx.RollbackAsync(ct);
                    await StampPublishedAsync(h.Country, h.BoxNo, ct);
                    skipped++;
                    continue;
                }

                // INSERT Head. Only column set the target is known to accept.
                await opb.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO usa.dbo.upcboxhead
                        (BoxNo, TrnDate, Time1, PreparedBy, PalletType, ToteID, LPMDT, PONo,
                         WHouse, Userid, Closed, Remarks)
                    VALUES
                        (@BoxNo, @TrnDate, @Time1, @PreparedBy, @PalletType, @ToteID, @LPMDT, @PONo,
                         @WHouse, @Userid, @Closed, @Remarks)",
                    new
                    {
                        h.BoxNo, h.TrnDate, h.Time1, h.PreparedBy, h.PalletType, h.ToteID, h.LPMDT,
                        h.PONo, h.WHouse, h.Userid, h.Closed, h.Remarks
                    },
                    transaction: tx, cancellationToken: ct));

                // INSERT Det rows (if any).
                if (detsByBox.TryGetValue(h.BoxNo, out var dets) && dets.Count > 0)
                {
                    foreach (var d in dets)
                    {
                        await opb.ExecuteAsync(new CommandDefinition(@"
                            INSERT INTO usa.dbo.upcboxdet
                                (BoxNo, Itemcode, SrNo, Qty, UPC, StoreId, Status, ToteID)
                            VALUES
                                (@BoxNo, @Itemcode, @SrNo, @Qty, @UPC, @StoreId, @Status, @ToteID)",
                            new { d.BoxNo, d.Itemcode, d.SrNo, d.Qty, d.UPC, d.StoreId, d.Status, d.ToteID },
                            transaction: tx, cancellationToken: ct));
                    }
                }

                await tx.CommitAsync(ct);
                await StampPublishedAsync(h.Country, h.BoxNo, ct);
                pushed++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 5) errors.Add($"{h.BoxNo}: {ex.Message}");
            }
        }

        var status  = failed == 0 ? "Success" : (pushed + skipped > 0 ? "PartialFailed" : "Failed");
        var msg     = $"Boxes push: {pushed} inserted, {skipped} skipped (already on target), {failed} failed.";
        var errText = errors.Count > 0 ? "First errors: " + string.Join(" | ", errors) : null;
        var logId   = await WriteLogRowAsync("(BoxesToWmsProd)", null, dest,
                        totalAllocatedQty: pushed + skipped,
                        status: status, error: errText, ct: ct,
                        origin: origin, actorOverride: actor);

        return new DataSyncResult(
            Ok: failed == 0,
            Message: msg + (errText is null ? "" : " " + errText),
            SyncId: logId,
            RowsCopied: pushed + skipped);
    }

    private async Task StampPublishedAsync(string country, string boxNo, CancellationToken ct)
    {
        await using var w = OpenWms();
        await w.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.WmsUPCBoxHead
                 SET PublishedTS = SYSDATETIME()
               WHERE Country = @c AND BoxNo = @b",
            new { c = country, b = boxNo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    private sealed class PushHeadRow
    {
        public string   Country     { get; set; } = "";
        public string   BoxNo       { get; set; } = "";
        public DateTime? TrnDate    { get; set; }
        public TimeSpan? Time1      { get; set; }
        public string?  PreparedBy  { get; set; }
        public string?  PalletType  { get; set; }
        public string?  ToteID      { get; set; }
        public DateTime? LPMDT      { get; set; }
        public string?  PONo        { get; set; }
        public string?  WHouse      { get; set; }
        public string?  Userid      { get; set; }
        public string?  Closed      { get; set; }
        public string?  Remarks     { get; set; }
    }

    private sealed class PushDetRow
    {
        public string   Country  { get; set; } = "";
        public string   BoxNo    { get; set; } = "";
        public string?  Itemcode { get; set; }
        public int      SrNo     { get; set; }
        public int      Qty      { get; set; }
        public string?  UPC      { get; set; }
        public string?  StoreId  { get; set; }
        public string?  Status   { get; set; }
        public string?  ToteID   { get; set; }
    }

    // ===================== PalletType master sync =====================

    private static readonly string[] PalletTypeColumns = new[]
    {
        "PalletType","TypeName","TrnDate","Reserved","GroupType","Exclude","Remarks",
        "Export","PalletPick","Report","Remarks1","Season","Order1","toTechno",
        "BuildCategoryMixAllow","PartofHOStock","ShopEligible","BlueBox",
        "DirectProduction","ShopPalletType","BuildSelItems","NonTrade",
        "ValidateHoStock","AllowInvalidItem","RegSIMExclude","PalletType_Shop",
        "NegativePurchase","PalletCategory","ToWHLocation","ExcludeFromLPR",
        "BuildingException"
    };

    /// <summary>Full refresh of dbo.WmsPalletType from bfldata.dbo.pallettype.
    /// PalletType master is the same across all countries, so we TRUNCATE +
    /// bulk-reload on every click — no incremental tracking needed.</summary>
    public async Task<DataSyncResult> SyncPalletTypeAsync(CancellationToken ct = default)
    {
        var cols = string.Join(", ", PalletTypeColumns.Select(c => $"[{c}]"));
        var srcSql = $"SELECT {cols} FROM bfldata.dbo.pallettype WITH (NOLOCK)";

        int rowsCopied = 0;
        string? error = null;

        try
        {
            // 1. Truncate Azure side first, inside its own connection.
            await using (var dest0 = OpenWms())
            {
                await dest0.ExecuteAsync(new CommandDefinition(
                    "TRUNCATE TABLE dbo.WmsPalletType",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            // 2. Open source reader and bulk-copy into Azure.
            await using var src = OpenOnPremBackup();
            using var cmd = new SqlCommand(srcSql, src) { CommandTimeout = CommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync(ct);

            await using var dest = OpenWms();
            using var bulk = new SqlBulkCopy(dest)
            {
                DestinationTableName = "dbo.WmsPalletType",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
                EnableStreaming      = true,
            };
            foreach (var c in PalletTypeColumns)
                bulk.ColumnMappings.Add(c, c);
            await bulk.WriteToServerAsync(reader, ct);
            rowsCopied = bulk.RowsCopied;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var syncId = await WriteLogRowAsync("(PalletType)", null, DataSyncDestination.WMSPalletType,
            totalAllocatedQty: rowsCopied,
            status: error is null ? "Success" : "Failed",
            error: error, ct);

        return error is null
            ? new DataSyncResult(true,
                $"PalletType master: {rowsCopied:N0} row(s) reloaded (truncate + insert).",
                syncId, rowsCopied)
            : new DataSyncResult(false, $"PalletType master sync failed: {error}", syncId, 0);
    }

    /// <summary>Full refresh of dbo.WMSBrandMaster from usa.dbo.BrandMaster
    /// (source column BrandName). Same shape as the PalletType sync — TRUNCATE
    /// + bulk-reload on every click.</summary>
    public async Task<DataSyncResult> SyncBrandMasterAsync(CancellationToken ct = default)
    {
        const string srcSql = @"
            SELECT DISTINCT BrandName
              FROM usa.dbo.BrandMaster WITH (NOLOCK)
             WHERE BrandName IS NOT NULL AND LTRIM(RTRIM(BrandName)) <> ''";

        int rowsCopied = 0;
        string? error = null;

        try
        {
            await using (var dest0 = OpenWms())
            {
                await dest0.ExecuteAsync(new CommandDefinition(
                    "TRUNCATE TABLE dbo.WMSBrandMaster",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            await using var src = OpenOnPremBackup();
            using var cmd = new SqlCommand(srcSql, src) { CommandTimeout = CommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync(ct);

            await using var dest = OpenWms();
            using var bulk = new SqlBulkCopy(dest)
            {
                DestinationTableName = "dbo.WMSBrandMaster",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
                EnableStreaming      = true,
            };
            bulk.ColumnMappings.Add("BrandName", "BrandName");
            await bulk.WriteToServerAsync(reader, ct);
            rowsCopied = bulk.RowsCopied;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var syncId = await WriteLogRowAsync("(BrandMaster)", null, DataSyncDestination.WMSBrandMaster,
            totalAllocatedQty: rowsCopied,
            status: error is null ? "Success" : "Failed",
            error: error, ct);

        return error is null
            ? new DataSyncResult(true,
                $"Brand master: {rowsCopied:N0} row(s) reloaded (truncate + insert).",
                syncId, rowsCopied)
            : new DataSyncResult(false, $"Brand master sync failed: {error}", syncId, 0);
    }
}
