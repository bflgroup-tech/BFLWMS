using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Holds a cross-instance mutex for one scheduled job. Dispose releases it.
/// <see cref="Acquired"/> is false when another instance already holds it.
/// </summary>
public sealed class ScheduledJobLock : IAsyncDisposable
{
    private readonly SqlConnection? _conn;
    private readonly string? _resource;

    internal ScheduledJobLock(SqlConnection? conn, string? resource, bool acquired)
    {
        _conn = conn; _resource = resource; Acquired = acquired;
    }

    public bool Acquired { get; }

    public async ValueTask DisposeAsync()
    {
        if (_conn is null) return;
        try
        {
            if (Acquired && _resource is not null)
            {
                var p = new DynamicParameters();
                p.Add("@Resource",  _resource);
                p.Add("@LockOwner", "Session");
                await _conn.ExecuteAsync(new CommandDefinition(
                    "sp_releaseapplock", p, commandType: CommandType.StoredProcedure));
            }
        }
        catch { /* closing the session releases it anyway */ }
        await _conn.DisposeAsync();
    }
}

/// <summary>
/// Generic activation + run-log access over the two shared Nightly Batches tables
/// (dbo.WmsRptCountryConfig, dbo.WmsRptJobRun), scoped by JobName.
///
/// MissingExcessSnapshotService / WeeklySalesFromGcpService / VolumeGroupWeeklyService
/// each carry their own copy of this boilerplate for historical reasons. New jobs use
/// this one instead of adding a fourth copy — pass JobName per call.
///
/// Jobs with no per-country dimension (OTS generate, tote sync, boxes push) use a
/// single row keyed by Country = "" — same convention WeeklySalesFromGCP adopted.
/// </summary>
public class ScheduledJobService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 600;

    /// <summary>Country key for jobs that have no per-country dimension.</summary>
    public const string SingleRowKey = "";

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    // ---------------- Cross-instance job lock ----------------

    /// <summary>
    /// Tries to take an exclusive, cross-instance lock for a scheduled job.
    ///
    /// EVERY App Service instance runs EVERY HostedService, so on a scaled-out app
    /// the same job fires once per instance. Observed on 2026-08-21: two OtsWeekly
    /// runs a second apart (07:01:11 and 07:01:12) from two different builds, which
    /// left DUPLICATE rows per (OTSDate, StoreID, DivCode) — because Generate is
    /// DELETE-then-INSERT and the two interleaved. Downstream, allocation builds its
    /// OTS lookup last-write-wins, so it then picked between the duplicates at random.
    ///
    /// sp_getapplock with @LockTimeout = 0 means the loser returns immediately
    /// rather than queueing to run the same work twice. Session-scoped, so the lock
    /// lives as long as the returned object holds its connection open.
    ///
    /// This is a safety net, not a substitute for the app being single-instance —
    /// but it makes a scale-out event harmless instead of data-corrupting.
    /// </summary>
    public async Task<ScheduledJobLock> TryAcquireJobLockAsync(string jobName, CancellationToken ct = default)
    {
        var resource = $"WmsScheduledJob:{jobName}";
        SqlConnection? c = null;
        try
        {
            c = OpenWms();
            var p = new DynamicParameters();
            p.Add("@Resource",    resource);
            p.Add("@LockMode",    "Exclusive");
            p.Add("@LockOwner",   "Session");
            p.Add("@LockTimeout", 0);
            p.Add("@ret", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            await c.ExecuteAsync(new CommandDefinition(
                "sp_getapplock", p, commandType: CommandType.StoredProcedure,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            // >= 0 granted (0 immediately, 1 after waiting); negative = not granted.
            var rc = p.Get<int>("@ret");
            if (rc >= 0) return new ScheduledJobLock(c, resource, true);

            await c.DisposeAsync();
            return new ScheduledJobLock(null, null, false);
        }
        catch
        {
            // A lock we cannot take must not stop the job — degrade to today's
            // behaviour (run anyway) rather than silently skipping the work.
            if (c is not null) await c.DisposeAsync();
            return new ScheduledJobLock(null, null, true);
        }
    }

    // ---------------- Activation (dbo.WmsRptCountryConfig) ----------------

    public async Task<List<RptCountryConfigRow>> GetConfigAsync(string jobName, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<RptCountryConfigRow>(new CommandDefinition(
            "SELECT Country, IsActive, UpdatedTS, UpdatedBy FROM dbo.WmsRptCountryConfig WHERE JobName = @j ORDER BY Country",
            new { j = jobName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Single-row jobs: the config row for Country = "". Returns an inactive
    /// placeholder when the row has never been created, so the admin page can
    /// still render a toggle that seeds it on first click.
    /// </summary>
    public async Task<RptCountryConfigRow> GetSingleRowAsync(string jobName, CancellationToken ct = default)
    {
        var rows = await GetConfigAsync(jobName, ct);
        return rows.FirstOrDefault(r => r.Country == SingleRowKey)
               ?? new RptCountryConfigRow(SingleRowKey, false, default, "");
    }

    /// <summary>
    /// Reads the live activation flag. Timers call this on every fire so a toggle
    /// flipped in the UI takes effect without a restart. Missing row = inactive,
    /// which keeps a newly deployed job switched off until someone enables it.
    /// </summary>
    public async Task<bool> IsActiveAsync(string jobName, string country = SingleRowKey, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT IsActive FROM dbo.WmsRptCountryConfig WHERE JobName = @j AND Country = @c",
            new { j = jobName, c = country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)) ?? false;
    }

    public async Task<List<string>> GetActiveCountriesAsync(string jobName, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT Country FROM dbo.WmsRptCountryConfig WHERE JobName = @j AND IsActive = 1 ORDER BY Country",
            new { j = jobName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetActiveAsync(string jobName, string country, bool isActive, string updatedBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            MERGE dbo.WmsRptCountryConfig AS t
            USING (SELECT @j AS JobName, @c AS Country) AS s
              ON t.JobName = s.JobName AND t.Country = s.Country
            WHEN MATCHED THEN
              UPDATE SET IsActive = @a, UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME()), UpdatedBy = @u
            WHEN NOT MATCHED THEN
              INSERT (JobName, Country, IsActive, UpdatedTS, UpdatedBy)
              VALUES (@j, @c, @a, DATEADD(hour, 4, SYSUTCDATETIME()), @u);",
            new { j = jobName, c = country, a = isActive, u = updatedBy },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ---------------- Run log (dbo.WmsRptJobRun) ----------------

    public async Task<long> StartRunAsync(string jobName, string mode, string? country, string triggeredBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
            INSERT INTO dbo.WmsRptJobRun (JobName, Country, Mode, StartTS, Status, TriggeredBy)
            OUTPUT INSERTED.RunId
            VALUES (@j, @c, @m, DATEADD(hour, 4, SYSUTCDATETIME()), 'Running', @t);",
            new { j = jobName, c = country, m = mode, t = triggeredBy },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task FinishRunAsync(long runId, string status, int? rowsProcessed, string? errorMessage, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsRptJobRun
               SET EndTS = DATEADD(hour, 4, SYSUTCDATETIME()), Status = @s,
                   RowsProcessed = @r, DatesProcessed = 1, ErrorMessage = @e
             WHERE RunId = @id;",
            new { id = runId, s = status, r = rowsProcessed, e = Trunc(errorMessage) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>
    /// Has this job completed successfully today (GST)? For a job that must run
    /// after another one on a fixed offset timer (not a wait-chain) — the second
    /// timer checks this before firing and defers/retries hourly if the upstream
    /// job hasn't landed yet today. StartTS is already stamped in GST by every
    /// writer, so no extra timezone conversion is needed here.
    /// </summary>
    public async Task<bool> HasSucceededTodayAsync(string jobName, CancellationToken ct = default)
    {
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(@"
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM dbo.WmsRptJobRun
                 WHERE JobName = @j AND Status = 'Success' AND CAST(StartTS AS DATE) = @today
            ) THEN 1 ELSE 0 END;",
            new { j = jobName, today = todayGst },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Last completed run per JobName, for the "Last run" column.</summary>
    /// <summary>
    /// True when this job already has a Success row for today (GST).
    ///
    /// The timers used an in-process "have I fired today" flag, which a restart
    /// resets — so every deploy after the fire time triggered a full catch-up run.
    /// On 2026-08-21 that produced five OtsWeekly runs: the 07:00 pair, then two
    /// more at 09:29 and another at 09:44, one per deploy restart.
    ///
    /// Asking the run log instead means a job fires at its scheduled time and a
    /// later restart does nothing, while a restart after a genuinely MISSED run
    /// still catches up — which is the only case the catch-up existed for. Being
    /// shared state, it also holds across instances rather than per-process.
    ///
    /// StartTS is already stored in GST (DATEADD(hour, 4, ...)), so the comparison
    /// is against the GST date, not UTC.
    /// </summary>
    public async Task<bool> HasSuccessfulRunTodayAsync(string jobName, CancellationToken ct = default)
    {
        var todayGst = DateTime.UtcNow.AddHours(4).Date;
        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
            SELECT TOP 1 1
              FROM dbo.WmsRptJobRun WITH (NOLOCK)
             WHERE JobName = @j
               AND Status = 'Success'
               AND CAST(StartTS AS date) = @dt",
            new { j = jobName, dt = todayGst },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return hit == 1;
    }

    public async Task<Dictionary<string, RptJobRunRow>> GetLastRunPerJobAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<RptJobRunRow>(new CommandDefinition(@"
            SELECT RunId, JobName, Country, Mode, StartTS, EndTS, Status,
                   RowsProcessed, DatesProcessed, ErrorMessage, TriggeredBy
              FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY JobName ORDER BY StartTS DESC, RunId DESC) AS rn
                      FROM dbo.WmsRptJobRun) x
             WHERE rn = 1;",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.ToDictionary(r => r.JobName, StringComparer.OrdinalIgnoreCase);
    }

    // WmsRptJobRun.ErrorMessage is nvarchar(max) on Azure but the on-prem mirror is
    // capped — keep messages short enough that neither side rejects the UPDATE.
    private static string? Trunc(string? s) =>
        s is null ? null : s.Length > 900 ? s[..900] : s;
}
