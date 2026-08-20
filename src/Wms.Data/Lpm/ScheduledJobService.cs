using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

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
