using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>Who holds the allocation lock, for the message shown to whoever is turned away.</summary>
public sealed record AllocationRunLockState(
    string?   RunKind,     // "Single" | "Bulk" | null when free
    string?   Scope,       // ContNo, or "batch #3" / "all batches"
    string?   HeldBy,
    DateTime? AcquiredTS,
    DateTime? HeartbeatTS)
{
    public bool IsHeld => !string.IsNullOrWhiteSpace(RunKind);

    /// <summary>One line naming the holder — used verbatim in the refusal message.</summary>
    public string Describe() =>
        !IsHeld ? "free"
        : $"{RunKind} allocation ({Scope}) started by {HeldBy ?? "?"} at {AcquiredTS:HH:mm}";
}

/// <summary>
/// Mutual exclusion between a single-container Process and a Bulk PO Allocation
/// run — in both directions.
///
/// They are not merely slow together. Both read the same OTS position and the
/// same prior-allocation totals and then write against them, so two runs
/// overlapping can allocate the same OTS twice. The observed symptom was milder
/// and easier to misread: a single Process timing out at 310s while a bulk run
/// walked 101 containers on the same on-prem server.
///
/// The lock lives in a table rather than sp_getapplock because a run opens and
/// closes dozens of short-lived connections and the lock has to outlive any one
/// of them; and rather than a static because App Service can run more than one
/// instance.
///
/// A holder refreshes HeartbeatTS as it works. A row whose heartbeat is older
/// than <see cref="StaleMinutes"/> is treated as abandoned and can be taken
/// over, so a crashed run cannot wedge allocation permanently.
/// </summary>
public class AllocationRunLockService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int CommandTimeoutSeconds = 60;
    private const string LockName = "PO_ALLOCATION";

    /// <summary>
    /// Well above a single container's worst observed runtime (~5 min), so a slow
    /// but healthy run is never stolen from. Only a genuinely dead one is.
    /// </summary>
    public const int StaleMinutes = 20;

    private SqlConnection Open()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private static DateTime NowGst() => DateTime.UtcNow.AddHours(4);

    /// <summary>Current holder, or a free state. Never throws — a missing table reads as free.</summary>
    public async Task<AllocationRunLockState> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = Open();
            var row = await c.QueryFirstOrDefaultAsync<AllocationRunLockState>(new CommandDefinition(
                @"SELECT RunKind, Scope, HeldBy, AcquiredTS, HeartbeatTS
                    FROM dbo.WMS_AllocationRunLock WITH (NOLOCK)
                   WHERE LockName = @n",
                new { n = LockName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (row is null) return new AllocationRunLockState(null, null, null, null, null);

            // A stale holder is reported as free — otherwise the UI would keep
            // showing a run that died hours ago as if it were still going.
            if (row.IsHeld && row.HeartbeatTS is { } hb && hb < NowGst().AddMinutes(-StaleMinutes))
                return new AllocationRunLockState(null, null, null, null, null);

            return row;
        }
        catch
        {
            // Table not deployed yet -> behave as before the lock existed.
            return new AllocationRunLockState(null, null, null, null, null);
        }
    }

    /// <summary>
    /// Take the lock, or report who has it. The UPDATE's WHERE clause is the whole
    /// mechanism: only one caller can match a free-or-stale row, so two simultaneous
    /// attempts cannot both succeed.
    /// </summary>
    public async Task<(bool Acquired, AllocationRunLockState Holder)> TryAcquireAsync(
        string runKind, string scope, CancellationToken ct = default)
    {
        var now = NowGst();
        try
        {
            await using var c = Open();
            var rows = await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WMS_AllocationRunLock
                   SET RunKind = @kind, Scope = @scope, HeldBy = @by,
                       AcquiredTS = @now, HeartbeatTS = @now
                 WHERE LockName = @n
                   AND (RunKind IS NULL OR HeartbeatTS IS NULL OR HeartbeatTS < @stale)",
                new
                {
                    n = LockName, kind = runKind, scope, by = user.Name, now,
                    stale = now.AddMinutes(-StaleMinutes),
                },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (rows > 0) return (true, new AllocationRunLockState(runKind, scope, user.Name, now, now));
            return (false, await GetStateAsync(ct));
        }
        catch
        {
            // Table not deployed -> do not block allocation on a missing lock.
            return (true, new AllocationRunLockState(runKind, scope, user.Name, now, now));
        }
    }

    /// <summary>Keep the lock alive during a long run. Silent on failure — a missed beat is not fatal.</summary>
    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = Open();
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.WMS_AllocationRunLock SET HeartbeatTS = @now WHERE LockName = @n AND HeldBy = @by",
                new { n = LockName, now = NowGst(), by = user.Name },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        catch { /* a missed heartbeat only costs staleness, and StaleMinutes has room */ }
    }

    /// <summary>
    /// Release. Deliberately unconditional on the holder: a run that crashed and
    /// left the row set must still be releasable by the next person to finish,
    /// and the stale window already covers the case nobody does.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = Open();
            await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WMS_AllocationRunLock
                   SET RunKind = NULL, Scope = NULL, HeldBy = NULL,
                       AcquiredTS = NULL, HeartbeatTS = NULL
                 WHERE LockName = @n",
                new { n = LockName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        catch { /* nothing useful left to do; the stale window will free it */ }
    }
}
