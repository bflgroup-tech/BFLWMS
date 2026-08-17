using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Core;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Weekly Volume Group refresh — driven by the Monday 04:00 GST scheduled
/// service (Wms.Web.Hosting.WeeklyVolumeGroupBatchService). Per-country
/// activation lives in dbo.WmsRptCountryConfig scoped by JobName; log rows
/// go to dbo.WmsRptJobRun. Both are shared across all Nightly Batches jobs
/// (MissingExcessSnapshot, WeeklySalesFromGCP, VolumeGroupWeekly).
///
/// For each active country:
///   BFLGROUP         -> OtsPoAllocationService.GenerateStoreDivGradesAsync
///                       writes dbo.StoreDivGrade for every country in one shot.
///   Specific country -> writes dbo.LPM_StoreDivGrade_Country filtered to
///                       that country.
/// </summary>
public class VolumeGroupWeeklyService(
    IOnPremConnectionResolver resolver,
    OtsPoAllocationService otsSvc,
    ICurrentUser user)
{
    public const string JobName = "VolumeGroupWeekly";
    private const int CommandTimeoutSeconds = 600;

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    // ---------------- Country config (shared WmsRptCountryConfig, scoped by JobName) ----------------

    public async Task<List<RptCountryConfigRow>> GetCountryConfigAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<RptCountryConfigRow>(new CommandDefinition(
            "SELECT Country, IsActive, UpdatedTS, UpdatedBy FROM dbo.WmsRptCountryConfig WHERE JobName = @j ORDER BY Country",
            new { j = JobName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<string>> GetActiveCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT Country FROM dbo.WmsRptCountryConfig WHERE JobName = @j AND IsActive = 1 ORDER BY Country",
            new { j = JobName }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetCountryActiveAsync(string country, bool isActive, CancellationToken ct = default)
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
            new { j = JobName, c = country, a = isActive, u = user.Name ?? "" },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ---------------- Job-run log (shared WmsRptJobRun) ----------------

    public async Task<long> StartJobRunAsync(string mode, string? country, string triggeredBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
            INSERT INTO dbo.WmsRptJobRun (JobName, Country, Mode, StartTS, Status, TriggeredBy)
            OUTPUT INSERTED.RunId
            VALUES (@j, @c, @m, DATEADD(hour, 4, SYSUTCDATETIME()), 'Running', @t);",
            new { j = JobName, c = country, m = mode, t = triggeredBy },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task FinishJobRunAsync(long runId, string status, int? rowsProcessed, string? errorMessage, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsRptJobRun
               SET EndTS = DATEADD(hour, 4, SYSUTCDATETIME()), Status = @s,
                   RowsProcessed = @r, DatesProcessed = 1, ErrorMessage = @e
             WHERE RunId = @id;",
            new { id = runId, s = status, r = rowsProcessed, e = errorMessage },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ---------------- The actual work ----------------

    /// <summary>
    /// Runs Generate Volume Group for every active country in this job's config,
    /// using the current GST month/year as the target. Each country gets its own
    /// row in WmsRptJobRun so a partial failure still leaves an audit trail for
    /// the countries that succeeded.
    /// </summary>
    /// <returns>Per-country outcome (country, rows persisted, error message).</returns>
    public async Task<List<(string Country, int Rows, string? Error)>> RunOnceAsync(
        string mode, string triggeredBy, CancellationToken ct = default)
    {
        var results = new List<(string Country, int Rows, string? Error)>();
        var countries = await GetActiveCountriesAsync(ct);
        if (countries.Count == 0) return results;

        var nowGst = DateTime.UtcNow.AddHours(4);
        var month  = nowGst.Month;
        var year   = nowGst.Year;

        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) break;
            var runId = await StartJobRunAsync(mode, country, triggeredBy, ct);
            try
            {
                // Passing BFLGROUP hits dbo.StoreDivGrade; any other country hits
                // dbo.LPM_StoreDivGrade_Country. Existing behaviour in the service.
                // Explicit actor: ICurrentUser cannot resolve a signed-in user from a
                // timer scope, and stamping GeneratedBy='anonymous' loses the audit.
                var actor = triggeredBy == "Timer" ? "system (scheduled)" : triggeredBy;
                var (rows, ungraded) = await otsSvc.GenerateStoreDivGradesAsync(month, year, country, ct, actor);
                // Stores that matched no band are written with a blank Grade. The run
                // did succeed, so keep the status green, but record the count — a
                // silent partial band set is exactly what hid the BFLGROUP problem.
                var note = ungraded > 0
                    ? $"{ungraded} store(s) matched no band and were graded blank — check AvgSalesPct ranges for {country}."
                    : null;
                await FinishJobRunAsync(runId, "Success", rows, note, ct);
                results.Add((country, rows, null));
            }
            catch (Exception ex)
            {
                await FinishJobRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
                results.Add((country, 0, ex.Message));
            }
        }
        return results;
    }
}
