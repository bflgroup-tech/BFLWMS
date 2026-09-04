using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>One row of dbo.WMS_BulkAllocationQueue.</summary>
public sealed record BulkAllocationQueueRow(
    int       Id,
    int       BatchNo,
    string    ContNo,
    string?   Country,
    string?   Warehouse,
    string?   RunOption,
    bool      IsActive,
    string?   Status,
    string?   Message,
    int?      RowsWritten,
    int?      AllocatedQty,
    int?      BlockedCount,
    DateTime? StartedTS,
    DateTime? CompletedTS,
    string?   RunBy);

/// <summary>One batch in the queue, for the batch picker.</summary>
public sealed record BulkAllocationBatchRow(
    int       BatchNo,
    int       Containers,
    int       Pending,
    int       Succeeded,
    int       Failed,
    DateTime? CreatedTS);

/// <summary>
/// One line of the Bulk PO Allocation Report: a (Container, PO, Division) with a
/// Qty / Alloc % / Ceiling % triple per country.
///
/// Countries is a dictionary rather than fixed columns because the country set is
/// whatever the scoped allocations actually reached — hard-coding it would leave
/// the report silently wrong the day a country is added or dropped.
/// </summary>
public sealed record BulkPoAllocationReportRow(
    string  Container,
    string  PONo,
    string? Division,
    int     DivCode,
    int     PoQty,
    Dictionary<string, BulkPoAllocationReportCell> ByCountry)
{
    public int TotalAllocated => ByCountry.Values.Sum(v => v.Qty);
}

/// <summary>
/// One country's cell. CeilingPct is null when no LPM_POAllocationMaxPct row
/// applies — meaning "no cap configured", which is different from a cap of 0 and
/// is rendered as blank rather than as a number.
/// </summary>
public sealed record BulkPoAllocationReportCell(
    int      Qty,
    decimal  AllocPct,
    decimal? CeilingPct)
{
    /// <summary>True when this country took more than its configured share of the PO.</summary>
    public bool OverCeiling => CeilingPct is > 0 && AllocPct > CeilingPct.Value;
}

/// <summary>Page inputs used for any queue row that does not override them.</summary>
public sealed record BulkAllocationDefaults(
    string                  Country,
    string                  Warehouse,
    RunOption               RunOption,
    // Used ONLY when the order imposes no restriction (AllocationCountry = 'All',
    // blank, or no order row). Must be the full SIM country list: 'All' means all,
    // so anything narrower here silently turns an unrestricted order into a
    // restricted one.
    IReadOnlyCollection<string> FallbackCountries,
    bool                    Validate,
    bool                    EcomManualPriority,
    bool                    TraceEnabled,
    bool                    BypassPass1b);

/// <summary>Progress ping for the bulk run — one per container, plus phase text.</summary>
public sealed record BulkAllocationProgress(int Done, int Total, string ContNo, string Phase);

/// <summary>Outcome of a delete-and-re-queue pass.</summary>
public sealed record BulkRequeueResult(
    int          Total,
    int          Deleted,        // had an allocation, now removed and re-queued
    int          NothingToDelete,// had none; re-queued anyway
    int          Blocked,        // synced / open box / scanned — left alone
    List<string> BlockedContnos);

/// <summary>Outcome of one whole bulk run.</summary>
public sealed record BulkAllocationRunResult(
    int Total, int Succeeded, int Skipped, int Failed,
    int TotalRows, int TotalQty, long ElapsedMs);

/// <summary>
/// "Run PO Allocation for All" — walks dbo.WMS_BulkAllocationQueue and puts each
/// container through the same Validate → Process → Save path the single-container
/// button uses, by composing ContainerAllocationService rather than duplicating it.
///
/// Three deliberate behaviours, none of them incidental:
///
///   1. Allocation countries are resolved PER CONTAINER from the order
///      (hodata..vUSAOrder.AllocationCountry), not from the page's multi-select.
///      A list of containers has no single country selection that could be right
///      for all of them, and the page's picks belong to whatever container is
///      typed in the box. The page selection is only a fallback for orders that
///      impose no restriction.
///
///   2. Containers already processed are SKIPPED, not reprocessed — and a
///      container processed under a different Run Option is skipped with that
///      named, mirroring the single-container "one process per container" rule.
///      Bulk must not become a way around a guard the single path enforces.
///
///   3. One container's failure does not stop the batch. Each is caught, recorded
///      against its own row, and the walk continues — otherwise a single bad
///      container at position 3 of 40 would waste the whole run.
///
/// Containers run SEQUENTIALLY. Each ProcessAllocationAsync fans out ~20 parallel
/// prefetch queries of its own; running containers concurrently on top of that
/// would multiply that load against the same on-prem server.
/// </summary>
public class BulkPoAllocationService(
    IOnPremConnectionResolver resolver,
    ICurrentUser user,
    ContainerAllocationService alloc)
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

    private static DateTime NowGst() => DateTime.UtcNow.AddHours(4);

    // ===================== Queue reads/writes =====================

    /// <summary>Queue rows, optionally narrowed to one batch.</summary>
    public async Task<List<BulkAllocationQueueRow>> GetQueueAsync(
        int? batchNo = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BulkAllocationQueueRow>(new CommandDefinition(@"
            SELECT Id, BatchNo, ContNo, Country, Warehouse, RunOption, IsActive,
                   Status, Message, RowsWritten, AllocatedQty, BlockedCount,
                   StartedTS, CompletedTS, RunBy
              FROM dbo.WMS_BulkAllocationQueue WITH (NOLOCK)
             WHERE (@batch IS NULL OR BatchNo = @batch)
             ORDER BY BatchNo DESC, IsActive DESC, Id",
            new { batch = batchNo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>One entry per batch for the picker, newest first.</summary>
    public async Task<List<BulkAllocationBatchRow>> GetBatchesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BulkAllocationBatchRow>(new CommandDefinition(@"
            SELECT BatchNo,
                   Containers = COUNT(*),
                   Pending    = SUM(CASE WHEN IsActive = 1 AND ISNULL(Status,'') <> 'Success' THEN 1 ELSE 0 END),
                   Succeeded  = SUM(CASE WHEN Status = 'Success' THEN 1 ELSE 0 END),
                   Failed     = SUM(CASE WHEN Status = 'Failed'  THEN 1 ELSE 0 END),
                   CreatedTS  = MIN(CreatedTS)
              FROM dbo.WMS_BulkAllocationQueue WITH (NOLOCK)
             GROUP BY BatchNo
             ORDER BY BatchNo DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Push a set of container numbers in as a NEW batch, and return its number.
    ///
    /// Containers already sitting in that same batch are not re-inserted (the
    /// unique index would reject them), but a container queued in an EARLIER batch
    /// is allowed through: batches are units of work, and a container that failed
    /// in one has to be re-runnable in the next.
    /// </summary>
    public async Task<(int BatchNo, int Inserted)> PushBatchAsync(
        IEnumerable<string> contnos, CancellationToken ct = default)
    {
        var list = contnos.Where(s => !string.IsNullOrWhiteSpace(s))
                          .Select(s => s.Trim())
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        if (list.Count == 0) return (0, 0);

        await using var c = OpenOnPremBackup();
        // The batch number and the insert share one statement so two people
        // pushing at the same moment cannot land on the same number.
        var batchNo = await c.ExecuteScalarAsync<int>(new CommandDefinition(@"
            DECLARE @batch INT;
            SELECT @batch = ISNULL(MAX(BatchNo), 0) + 1 FROM dbo.WMS_BulkAllocationQueue WITH (TABLOCKX);

            INSERT INTO dbo.WMS_BulkAllocationQueue (BatchNo, ContNo)
            SELECT @batch, LTRIM(RTRIM(value))
              FROM STRING_SPLIT(@csv, ',')
             WHERE LTRIM(RTRIM(value)) <> '';

            SELECT @batch;",
            new { csv = string.Join(",", list) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return (batchNo, list.Count);
    }

    /// <summary>
    /// Clear results so the list can be re-run. Success rows are left alone by
    /// default — re-running them would only skip, and wiping their result would
    /// lose the record of what the batch actually achieved.
    /// </summary>
    public async Task<int> ResetQueueAsync(
        int? batchNo = null, bool includeSucceeded = false, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteAsync(new CommandDefinition($@"
            UPDATE dbo.WMS_BulkAllocationQueue
               SET Status = NULL, Message = NULL, RowsWritten = NULL, AllocatedQty = NULL,
                   BlockedCount = NULL, StartedTS = NULL, CompletedTS = NULL, RunBy = NULL
             WHERE (@batch IS NULL OR BatchNo = @batch)
               {(includeSucceeded ? "" : "AND ISNULL(Status,'') <> 'Success'")}",
            new { batch = batchNo },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>
    /// Delete each container's existing allocation and put its queue row back to
    /// Pending, so a whole batch can be re-run from scratch.
    ///
    /// Deleting goes through ContainerAllocationService.ResetFinalAsync, which
    /// already refuses when the container is synced to Azure, has an open box, or
    /// has scan data. Those refusals are recorded against the row and the container
    /// is LEFT AS IT WAS — a container whose allocation is already downstream must
    /// not be quietly re-queued, because re-running it would not undo what shipped.
    /// </summary>
    public async Task<BulkRequeueResult> DeleteAndRequeueAsync(
        BulkAllocationDefaults defaults,
        int? batchNo = null,
        IProgress<BulkAllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var queue = (await GetQueueAsync(batchNo, ct)).Where(q => q.IsActive).ToList();
        int deleted = 0, blocked = 0, nothing = 0, done = 0;
        var blockedContnos = new List<string>();

        foreach (var q in queue)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var contno    = q.ContNo.Trim();
            var country   = string.IsNullOrWhiteSpace(q.Country) ? defaults.Country : q.Country.Trim();
            var runOption = ParseRunOption(q.RunOption) ?? defaults.RunOption;
            progress?.Report(new BulkAllocationProgress(done, queue.Count, contno, "deleting"));

            try
            {
                var rows = await alloc.ResetFinalAsync(country, contno, runOption, ct);
                if (rows > 0) deleted++; else nothing++;
                await SetStatusAsync(q.Id, null,
                    rows > 0 ? $"Previous allocation deleted ({rows:N0} rows) — re-queued." : null, ct);
            }
            catch (Exception ex)
            {
                // Left at its existing Status on purpose: it is still allocated.
                blocked++;
                blockedContnos.Add(contno);
                await SetStatusAsync(q.Id, q.Status, $"Not re-queued — {ex.Message}", ct);
            }
        }

        return new BulkRequeueResult(queue.Count, deleted, nothing, blocked, blockedContnos);
    }

    /// <summary>Write Status/Message directly. A null status means Pending.</summary>
    private async Task SetStatusAsync(int id, string? status, string? message, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WMS_BulkAllocationQueue
               SET Status = @st, Message = @msg,
                   RowsWritten = NULL, AllocatedQty = NULL, BlockedCount = NULL,
                   StartedTS = NULL, CompletedTS = NULL
             WHERE Id = @id",
            new
            {
                id, st = status,
                msg = message is { Length: > 1000 } ? message[..1000] : message,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    private async Task MarkStartedAsync(int id, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WMS_BulkAllocationQueue
               SET Status = 'Running', Message = NULL, StartedTS = @ts, CompletedTS = NULL, RunBy = @u
             WHERE Id = @id",
            new { id, ts = NowGst(), u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    private async Task MarkDoneAsync(
        int id, string status, string? message, int? rows, int? qty, int? blocked, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WMS_BulkAllocationQueue
               SET Status = @st, Message = @msg, RowsWritten = @rows,
                   AllocatedQty = @qty, BlockedCount = @blk, CompletedTS = @ts
             WHERE Id = @id",
            new
            {
                id, st = status, ts = NowGst(), rows, qty, blk = blocked,
                // The column is NVARCHAR(1000); a raw SQL exception can run longer
                // than that and would fail the status write itself, losing the reason.
                msg = message is { Length: > 1000 } ? message[..1000] : message,
            },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ===================== Report =====================

    /// <summary>
    /// Bulk PO Allocation Report — allocated qty, share of the PO, and the
    /// configured ceiling, per country, for each (Container, PO, Division).
    ///
    /// The share denominator is the PO qty from usa.usaorgfile_LPM, NOT the sum of
    /// what was allocated. Blocked and undistributed units are part of the PO, so
    /// dividing by the allocated total would quietly rebase every percentage and
    /// make a container look fully within its ceiling when it was not.
    ///
    /// Ceiling % resolves (Country, DivCode) then (Country, 0), the same order the
    /// allocation engine uses — if the report resolved it differently it would
    /// contradict the thing it is reporting on.
    /// </summary>
    public async Task<List<BulkPoAllocationReportRow>> GetReportAsync(
        int? batchNo = null, string? contnoFilter = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();

        // Scope: a batch's containers, or every container that has allocations.
        string[]? scopeContnos = null;
        if (batchNo is not null)
        {
            scopeContnos = (await c.QueryAsync<string>(new CommandDefinition(
                "SELECT ContNo FROM dbo.WMS_BulkAllocationQueue WITH (NOLOCK) WHERE BatchNo = @b",
                new { b = batchNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (scopeContnos.Length == 0) return new();
        }

        var like = string.IsNullOrWhiteSpace(contnoFilter) ? null : "%" + contnoFilter.Trim() + "%";
        var contnoCsv = scopeContnos is null ? null : string.Join(",", scopeContnos);

        // Allocated per (ContNo, PO, DivCode, Country).
        var allocSql = $@"
            {(contnoCsv is null ? "" : @"
            SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ContNo INTO #rptScope FROM STRING_SPLIT(@contnoCsv, ',');
            CREATE CLUSTERED INDEX IX_rptScope ON #rptScope(ContNo);")}

            SELECT ContNo   = d.TcmContno,
                   PONo     = ISNULL(d.ORAPONo, ''),
                   DivCode  = ISNULL(d.DivCode, 0),
                   Division = MAX(ISNULL(d.Division, '')),
                   Country  = d.Country,
                   Qty      = SUM(CAST(ISNULL(d.AllocatedQty, 0) AS int))
              FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
              {(contnoCsv is null ? "" : "INNER JOIN #rptScope s ON s.ContNo = d.TcmContno")}
             WHERE (@like IS NULL OR d.TcmContno LIKE @like)
             GROUP BY d.TcmContno, ISNULL(d.ORAPONo, ''), ISNULL(d.DivCode, 0), d.Country
            HAVING SUM(CAST(ISNULL(d.AllocatedQty, 0) AS int)) <> 0;";

        var allocRows = (await c.QueryAsync<(string ContNo, string PONo, int DivCode, string Division, string Country, int Qty)>(
            new CommandDefinition(allocSql, new { contnoCsv, like },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();
        if (allocRows.Count == 0) return new();

        // PO qty per (ContNo, PO, DivCode) — the share denominator.
        var poCsv = string.Join(",", allocRows.Select(r => r.ContNo)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var poRows = (await c.QueryAsync<(string ContNo, string PONo, int DivCode, int PoQty)>(
            new CommandDefinition(@"
                SELECT DISTINCT CAST(value AS VARCHAR(50)) AS ContNo INTO #rptPoScope FROM STRING_SPLIT(@poCsv, ',');
                CREATE CLUSTERED INDEX IX_rptPoScope ON #rptPoScope(ContNo);

                SELECT ContNo  = u.ContNo,
                       PONo    = ISNULL(u.OraPONo, ''),
                       DivCode = ISNULL(v.DivID, 0),
                       PoQty   = SUM(CAST(ISNULL(u.orgqty, 0) AS int))
                  FROM usa.dbo.usaorgfile_LPM u WITH (NOLOCK)
                  INNER JOIN #rptPoScope s ON s.ContNo = u.ContNo
                  LEFT  JOIN datareporting.dbo.vupc_subclass v WITH (NOLOCK) ON v.itemcode = u.ItemCode
                 GROUP BY u.ContNo, ISNULL(u.OraPONo, ''), ISNULL(v.DivID, 0);",
                new { poCsv }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var poQtyByKey = new Dictionary<(string, string, int), int>();
        foreach (var p in poRows)
            poQtyByKey[(p.ContNo.Trim().ToUpperInvariant(), p.PONo, p.DivCode)] = p.PoQty;

        // Ceilings, resolved the same way the allocation engine resolves them.
        var ceilings = new Dictionary<(string Country, int DivCode), decimal>();
        try
        {
            var rows = await c.QueryAsync<(string Country, int DivCode, decimal Pct)>(new CommandDefinition(
                @"SELECT Country, ISNULL(DivCode, 0) AS DivCode, POAllocationMaxPct
                    FROM LPMSIM.dbo.LPM_POAllocationMaxPct WITH (NOLOCK)
                   WHERE Country IS NOT NULL AND POAllocationMaxPct > 0",
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            foreach (var r in rows)
                ceilings[(r.Country.Trim().ToUpperInvariant(), r.DivCode)] = r.Pct;
        }
        catch { /* table not deployed -> every Ceiling % is blank, not zero */ }

        decimal? CeilingFor(string country, int divCode)
        {
            var key = country.Trim().ToUpperInvariant();
            if (ceilings.TryGetValue((key, divCode), out var exact)) return exact;
            if (ceilings.TryGetValue((key, 0), out var wide)) return wide;
            return null;
        }

        // Pivot.
        return allocRows
            .GroupBy(r => (r.ContNo, r.PONo, r.DivCode))
            .Select(g =>
            {
                var poQty = poQtyByKey.GetValueOrDefault(
                    (g.Key.ContNo.Trim().ToUpperInvariant(), g.Key.PONo, g.Key.DivCode), 0);

                var cells = new Dictionary<string, BulkPoAllocationReportCell>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in g)
                {
                    var pct = poQty > 0 ? Math.Round(r.Qty * 100m / poQty, 2) : 0m;
                    cells[r.Country] = new BulkPoAllocationReportCell(r.Qty, pct, CeilingFor(r.Country, g.Key.DivCode));
                }

                return new BulkPoAllocationReportRow(
                    g.Key.ContNo, g.Key.PONo,
                    g.Select(x => x.Division).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    g.Key.DivCode, poQty, cells);
            })
            .OrderBy(r => r.Container, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.PONo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Division, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ===================== The run =====================

    /// <summary>
    /// Run the queue, or just one batch of it when batchNo is given.
    ///
    /// <paramref name="onlyNotAllocated"/> narrows the walk to containers that came
    /// away with nothing — Failed, or never attempted. It deliberately leaves
    /// Skipped rows alone: a container is skipped because it ALREADY has an
    /// allocation (under this Run Option or another one), so re-running it would
    /// only skip again. "Not allocated" means no allocation exists, not "the last
    /// run did not write one".
    /// </summary>
    public async Task<BulkAllocationRunResult> RunAllAsync(
        BulkAllocationDefaults defaults,
        int? batchNo = null,
        bool onlyNotAllocated = false,
        IProgress<BulkAllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var queue = (await GetQueueAsync(batchNo, ct))
            .Where(q => q.IsActive && IsRunnable(q.Status, onlyNotAllocated))
            .ToList();

        int ok = 0, skipped = 0, failed = 0, totalRows = 0, totalQty = 0, done = 0;

        foreach (var q in queue)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            var contno    = q.ContNo.Trim();
            var country   = string.IsNullOrWhiteSpace(q.Country)   ? defaults.Country   : q.Country.Trim();
            var warehouse = string.IsNullOrWhiteSpace(q.Warehouse) ? defaults.Warehouse : q.Warehouse.Trim();
            var runOption = ParseRunOption(q.RunOption) ?? defaults.RunOption;

            void Report(string phase) =>
                progress?.Report(new BulkAllocationProgress(done, queue.Count, contno, phase));

            Report("starting");

            try
            {
                await MarkStartedAsync(q.Id, ct);

                // ---- already processed? ----
                var status = await alloc.GetStatusAsync(country, contno, ct);
                var existingForRo = RowsFor(status, runOption);
                if (existingForRo > 0)
                {
                    skipped++;
                    await MarkDoneAsync(q.Id, "Skipped",
                        $"Already has {existingForRo:N0} {runOption} row(s).", null, null, null, ct);
                    continue;
                }

                var otherRo = OtherRunOptionsWithRows(status, runOption);
                if (otherRo.Count > 0)
                {
                    skipped++;
                    await MarkDoneAsync(q.Id, "Skipped",
                        $"Already processed under a different Run Option ({string.Join(", ", otherRo)}). " +
                        "Delete that first.", null, null, null, ct);
                    continue;
                }

                // ---- allocation countries, per container, from the order ----
                Report("resolving allocation countries");
                var permitted = await alloc.GetAllocationCountriesForContainerAsync(
                    contno, defaults.FallbackCountries, ct);
                var allocCountries = permitted.Restricted
                    ? permitted.Allowed
                    : defaults.FallbackCountries.ToList();

                // Recorded on the result row: "restricted to X by the order" and
                // "unrestricted, so all countries" produce identical country lists
                // when the order happens to name one country, and telling them apart
                // afterwards is exactly what was needed to spot the fallback bug.
                var countrySource = permitted.Restricted
                    ? $"AllocationCountry = '{permitted.RawValue}'"
                    : $"order does not restrict{(permitted.RawValue is null ? "" : $" (AllocationCountry = '{permitted.RawValue}')")}";

                if (allocCountries.Count == 0)
                {
                    failed++;
                    await MarkDoneAsync(q.Id, "Failed",
                        "No allocation countries — the order names none and no fallback is selected.",
                        null, null, null, ct);
                    continue;
                }

                // ---- validate ----
                if (defaults.Validate)
                {
                    Report("validating");
                    var v = await alloc.ValidateAsync(
                        country, contno, null, runOption, allocCountries,
                        defaults.EcomManualPriority, ct);
                    if (!v.Ok)
                    {
                        var firstBad = v.Steps.FirstOrDefault(s => !s.Ok);
                        failed++;
                        await MarkDoneAsync(q.Id, "Failed",
                            $"Validation failed: {firstBad?.Label} — {firstBad?.Detail}",
                            null, null, null, ct);
                        continue;
                    }
                }

                // ---- process ----
                Report("allocating");
                var res = await alloc.ProcessAllocationAsync(
                    contno, null, runOption, allocCountries,
                    defaults.EcomManualPriority, defaults.TraceEnabled, defaults.BypassPass1b, ct);

                if (res.Allocations.Count == 0 && res.Blocked.Count == 0)
                {
                    failed++;
                    await MarkDoneAsync(q.Id, "Failed",
                        "No allocation rows produced. Check LPM_EOM_Output + LPM_SKUMaxRule coverage.",
                        0, 0, 0, ct);
                    continue;
                }

                // ---- save ----
                Report("saving");
                await alloc.SaveFinalDirectAsync(
                    country, contno, string.Join(",", allocCountries), warehouse,
                    res.Allocations, runOption, res.Blocked, null, ct);

                var qty = res.Allocations.Sum(r => r.AllocQty);
                ok++;
                totalRows += res.Allocations.Count;
                totalQty  += qty;
                await MarkDoneAsync(q.Id, "Success",
                    $"{allocCountries.Count} country(ies): {string.Join(", ", allocCountries)} — {countrySource}.",
                    res.Allocations.Count, qty, res.Blocked.Count, ct);
            }
            catch (Exception ex)
            {
                // One container must not take the batch down with it.
                failed++;
                try { await MarkDoneAsync(q.Id, "Failed", ex.Message, null, null, null, CancellationToken.None); }
                catch { /* the status write itself failed — nothing useful left to do */ }
            }
        }

        return new BulkAllocationRunResult(
            queue.Count, ok, skipped, failed, totalRows, totalQty, sw.ElapsedMilliseconds);
    }

    // ===================== helpers =====================

    /// <summary>
    /// Which queue rows a run picks up. Shared by the service and the page so the
    /// button's count and the walk can never disagree about what will be attempted.
    /// </summary>
    public static bool IsRunnable(string? status, bool onlyNotAllocated)
    {
        if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return false;
        if (!onlyNotAllocated) return true;
        // Skipped == an allocation already exists, so it is not "not allocated".
        return !string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase);
    }

    private static RunOption? ParseRunOption(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
        : Enum.TryParse<RunOption>(s.Trim(), ignoreCase: true, out var ro) ? ro
        : null;

    private static int RowsFor(AllocationStatus? s, RunOption ro) => ro switch
    {
        Lpm.RunOption.RoundRobin           => s?.RoundRobinRows           ?? 0,
        Lpm.RunOption.FillSKUMaxRoundRobin => s?.FillSKUMaxRoundRobinRows ?? 0,
        Lpm.RunOption.FillMinMinPlusOthers => s?.FillMinMinPlusOthersRows ?? 0,
        _                                  => s?.FillSkuMaxRows           ?? 0,
    };

    private static List<string> OtherRunOptionsWithRows(AllocationStatus? s, RunOption ro)
    {
        var others = new List<string>();
        foreach (var candidate in Enum.GetValues<RunOption>())
        {
            if (candidate == ro) continue;
            var n = RowsFor(s, candidate);
            if (n > 0) others.Add($"{candidate} ({n:N0})");
        }
        return others;
    }
}
