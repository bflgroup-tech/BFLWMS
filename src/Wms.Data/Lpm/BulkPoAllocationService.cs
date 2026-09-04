using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>One row of dbo.WMS_BulkAllocationQueue.</summary>
public sealed record BulkAllocationQueueRow(
    int       Id,
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

/// <summary>Page inputs used for any queue row that does not override them.</summary>
public sealed record BulkAllocationDefaults(
    string                  Country,
    string                  Warehouse,
    RunOption               RunOption,
    IReadOnlyCollection<string> FallbackCountries,  // used when the order does not restrict
    bool                    Validate,
    bool                    EcomManualPriority,
    bool                    TraceEnabled,
    bool                    BypassPass1b);

/// <summary>Progress ping for the bulk run — one per container, plus phase text.</summary>
public sealed record BulkAllocationProgress(int Done, int Total, string ContNo, string Phase);

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

    public async Task<List<BulkAllocationQueueRow>> GetQueueAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<BulkAllocationQueueRow>(new CommandDefinition(@"
            SELECT Id, ContNo, Country, Warehouse, RunOption, IsActive,
                   Status, Message, RowsWritten, AllocatedQty, BlockedCount,
                   StartedTS, CompletedTS, RunBy
              FROM dbo.WMS_BulkAllocationQueue WITH (NOLOCK)
             ORDER BY IsActive DESC, Id",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Clear results so the list can be re-run. Success rows are left alone by
    /// default — re-running them would only skip, and wiping their result would
    /// lose the record of what the batch actually achieved.
    /// </summary>
    public async Task<int> ResetQueueAsync(bool includeSucceeded = false, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteAsync(new CommandDefinition($@"
            UPDATE dbo.WMS_BulkAllocationQueue
               SET Status = NULL, Message = NULL, RowsWritten = NULL, AllocatedQty = NULL,
                   BlockedCount = NULL, StartedTS = NULL, CompletedTS = NULL, RunBy = NULL
             WHERE 1 = 1 {(includeSucceeded ? "" : "AND ISNULL(Status,'') <> 'Success'")}",
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

    // ===================== The run =====================

    public async Task<BulkAllocationRunResult> RunAllAsync(
        BulkAllocationDefaults defaults,
        IProgress<BulkAllocationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var queue = (await GetQueueAsync(ct))
            .Where(q => q.IsActive && !string.Equals(q.Status, "Success", StringComparison.OrdinalIgnoreCase))
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
                    $"{allocCountries.Count} country(ies): {string.Join(", ", allocCountries)}.",
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
