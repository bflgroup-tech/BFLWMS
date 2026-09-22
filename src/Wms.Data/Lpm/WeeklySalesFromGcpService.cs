using System.Data;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Wms.Core;
using Wms.Data.Configuration;
using Wms.Data.Gcp;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record WeeklySalesGcpRow(string StoreId, int DivCode, int Year1, int Month1, int Week, int? SalesQty, decimal? SalesAmt, decimal? Turns);

/// <summary>
/// Pulls only the current + last week of the sales feed from BigQuery
/// (mvp-data-bi.cdm_silver.it_sales_qty — the two most recent distinct
/// (CalendarYear, CalendarWeek) pairs in that table) and replaces those two weeks'
/// worth of rows in dbo.LPM_Weekly_SalesAmt — the same table OtsPoAllocationService's
/// "Generate Volume Group" reads for weighted monthly sales. Older weeks are never
/// re-touched once written, since only the trailing two weeks in the source actually
/// change. The source has no country column, so the same BigQuery result set is
/// written for every active country.
///
/// Writes go through the OnPremBackup connection (same one MissingExcessSnapshotService
/// uses) rather than a per-country connection string — no per-country
/// "{Country}_DB_ConnectionString" is actually configured in Azure for this on-prem
/// target, only the single OnPremBackupDB_ConnectionString. "Country" here is purely
/// the activation label in WmsRptCountryConfig, not a distinct physical connection.
///
/// Country activation + job-run log reuse dbo.WmsRptCountryConfig / dbo.WmsRptJobRun on
/// the Azure WMS DB (same tables MissingExcessSnapshotService uses), scoped by JobName.
/// </summary>
public class WeeklySalesFromGcpService(IOnPremConnectionResolver resolver, IOptions<GcpBigQueryOptions> gcpOpts, IConfiguration configuration, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "WeeklySalesFromGCP";

    // Only the current + last week actually change once published, so pull just the
    // two most recent distinct (CalendarYear, CalendarWeek) pairs rather than the
    // full 300K+ row history.
    private const string SourceQuery = @"
        WITH recent_weeks AS (
          SELECT DISTINCT CalendarYear, CalendarWeek
            FROM cdm_silver.it_sales_qty
           ORDER BY CalendarYear DESC, CalendarWeek DESC
           LIMIT 2
        )
        SELECT s.storeid, s.DivCode, s.CalendarYear, s.CalendarMonth, s.CalendarWeek, s.Soldqty, s.NetSalesExVAT, s.Turns
          FROM cdm_silver.it_sales_qty s
          JOIN recent_weeks w
            ON s.CalendarYear = w.CalendarYear AND s.CalendarWeek = w.CalendarWeek";

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    // ====================== Country config (shared WmsRptCountryConfig, scoped by JobName) ======================

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
            new { j = JobName, c = country, a = isActive, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ====================== Job-run log (shared WmsRptJobRun) ======================

    public async Task<long> StartJobRunAsync(string mode, string? country, string triggeredBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
            INSERT INTO dbo.WmsRptJobRun (JobName, Country, Mode, StartTS, Status, TriggeredBy)
            OUTPUT INSERTED.RunId
            VALUES (@j, @c, @m, DATEADD(hour, 4, SYSUTCDATETIME()), 'Running', @t);",
            new { j = JobName, c = country, m = mode, t = triggeredBy },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return id;
    }

    public async Task FinishJobRunAsync(long runId, string status, int? rowsProcessed, int? datesProcessed, string? errorMessage, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsRptJobRun
               SET EndTS = DATEADD(hour, 4, SYSUTCDATETIME()), Status = @s, RowsProcessed = @r,
                   DatesProcessed = @d, ErrorMessage = @e
             WHERE RunId = @id;",
            new { id = runId, s = status, r = rowsProcessed, d = datesProcessed, e = errorMessage },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>True if a Timer-triggered run of the given mode already started today
    /// (GST). Guards against re-firing after an app restart resets the hosted service's
    /// in-memory "already ran today" tracker — restarts happen mid-day on every deploy.</summary>
    public async Task<bool> HasFiredTodayAsync(string mode, DateTime todayGst, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var lastStart = await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(StartTS) FROM dbo.WmsRptJobRun WHERE JobName = @j AND Mode = @m AND TriggeredBy = 'Timer'",
            new { j = JobName, m = mode }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return lastStart is not null && lastStart.Value.Date == todayGst.Date;
    }

    // ====================== BigQuery fetch ======================

    // BigQuery's client library returns NUMERIC/BIGNUMERIC columns as a
    // BigQueryNumeric struct, which doesn't implement IConvertible — Convert.ToInt32/
    // ToDecimal throw on it. Route every numeric column through ToString() + Parse
    // instead, which works uniformly across long, double, BigQueryNumeric, and string.
    private static int? ParseInt(object? value) =>
        value is null ? null : (int)decimal.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? ParseDecimal(object? value) =>
        value is null ? null : decimal.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture);

    // The 01:00 GST Timer fire is consistently the first request the App Service sees
    // after its overnight idle window, so it's the request that pays for a full cold
    // start — runtime init, DI, and Key-Vault-referenced credential resolution
    // (GCP_SERVICE_ACCOUNT_JSON / BigQuery:CredentialsPath) — which can run well past
    // a few seconds. A manual "Refresh Now" click always lands on an already-warm
    // instance, which is why only the scheduled run fails. Retry client creation with
    // backoff generous enough to outlast a full cold start before giving up.
    private static readonly TimeSpan[] CredentialRetryDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40),
         TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90)];

    private static async Task<BigQueryClient> CreateBigQueryClientAsync(
        string projectId, GcpBigQueryOptions opts, IConfiguration configuration, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var serviceAccountJson = configuration["GCP_SERVICE_ACCOUNT_JSON"];
                return !string.IsNullOrWhiteSpace(serviceAccountJson)
                    ? await BigQueryClient.CreateAsync(projectId, GoogleCredential.FromJson(serviceAccountJson))
                    : !string.IsNullOrWhiteSpace(opts.CredentialsPath)
                        ? await BigQueryClient.CreateAsync(projectId, GoogleCredential.FromFile(opts.CredentialsPath))
                        : await BigQueryClient.CreateAsync(projectId);
            }
            catch when (attempt < CredentialRetryDelays.Length)
            {
                await Task.Delay(CredentialRetryDelays[attempt], ct);
            }
        }
    }

    public async Task<List<WeeklySalesGcpRow>> FetchFromBigQueryAsync(CancellationToken ct = default)
    {
        var opts = gcpOpts.Value;
        var projectId = configuration["GCP_PROJECT_ID"];
        if (string.IsNullOrWhiteSpace(projectId)) projectId = opts.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(
                "BigQuery is not configured — set GCP_PROJECT_ID (or BigQuery:ProjectId) in configuration.");

        var client = await CreateBigQueryClientAsync(projectId, opts, configuration, ct);

        var result = await client.ExecuteQueryAsync(SourceQuery, parameters: null, cancellationToken: ct);

        var rows = new List<WeeklySalesGcpRow>();
        foreach (var row in result)
        {
            rows.Add(new WeeklySalesGcpRow(
                StoreId:  row["storeid"]?.ToString() ?? "",
                DivCode:  ParseInt(row["DivCode"]) ?? 0,
                Year1:    ParseInt(row["CalendarYear"]) ?? 0,
                Month1:   ParseInt(row["CalendarMonth"]) ?? 0,
                Week:     ParseInt(row["CalendarWeek"]) ?? 0,
                SalesQty: ParseInt(row["Soldqty"]),
                SalesAmt: ParseDecimal(row["NetSalesExVAT"]),
                Turns:    ParseDecimal(row["Turns"])));
        }

        // The source can carry more than one row per (StoreId, DivCode, Year1, Month1,
        // Week) — that's LPM_Weekly_SalesAmt's primary key, so a duplicate there throws
        // a PK violation on the INSERT. Sum duplicates into one row per key rather than
        // arbitrarily dropping one.
        return rows
            .GroupBy(r => (r.StoreId, r.DivCode, r.Year1, r.Month1, r.Week))
            .Select(g => new WeeklySalesGcpRow(
                g.Key.StoreId, g.Key.DivCode, g.Key.Year1, g.Key.Month1, g.Key.Week,
                SalesQty: g.Any(r => r.SalesQty.HasValue) ? g.Sum(r => r.SalesQty ?? 0) : null,
                SalesAmt: g.Any(r => r.SalesAmt.HasValue) ? g.Sum(r => r.SalesAmt ?? 0) : null,
                Turns:    g.Any(r => r.Turns.HasValue) ? g.Sum(r => r.Turns ?? 0) : null))
            .ToList();
    }

    // ====================== Replace current + last week in one country's on-prem LPM_Weekly_SalesAmt ======================
    // Bulk-copies the fetched rows (already scoped to the 2 most recent weeks) into a
    // session-scoped #Staging temp table, deletes any existing rows for those same
    // (Year1, Week) pairs, then inserts the staged rows fresh — guarantees no stale
    // row survives even if BigQuery dropped a (StoreID, DivCode) combo that used to
    // report for that week, which a MERGE (matched-key only) would silently leave behind.

    private const string CreateStagingSql = @"
        CREATE TABLE #Staging (
            StoreID  NVARCHAR(50)  NOT NULL,
            DivCode  INT           NOT NULL,
            Year1    INT           NOT NULL,
            Month1   INT           NOT NULL,
            Week     INT           NOT NULL,
            SalesQty INT           NULL,
            SalesAmt DECIMAL(18,2) NULL,
            Turns    DECIMAL(18,2) NULL
        );";

    private const string DeleteRecentWeeksSql = @"
        DELETE t
          FROM dbo.LPM_Weekly_SalesAmt AS t
          JOIN (SELECT DISTINCT Year1, Week FROM #Staging) AS w
            ON t.Year1 = w.Year1 AND t.Week = w.Week;";

    private const string InsertFromStagingSql = @"
        INSERT INTO dbo.LPM_Weekly_SalesAmt (StoreID, DivCode, Year1, Month1, Week, SalesQty, SalesAmt, CreateTS, Turns)
        SELECT StoreID, DivCode, Year1, Month1, Week, SalesQty, SalesAmt, DATEADD(hour, 4, SYSUTCDATETIME()), Turns
          FROM #Staging;";

    private static DataTable ToStagingTable(IReadOnlyList<WeeklySalesGcpRow> rows)
    {
        var table = new DataTable();
        table.Columns.Add("StoreID", typeof(string));
        table.Columns.Add("DivCode", typeof(int));
        table.Columns.Add("Year1", typeof(int));
        table.Columns.Add("Month1", typeof(int));
        table.Columns.Add("Week", typeof(int));
        table.Columns.Add("SalesQty", typeof(int));
        table.Columns.Add("SalesAmt", typeof(decimal));
        table.Columns.Add("Turns", typeof(decimal));
        foreach (var r in rows)
            table.Rows.Add(r.StoreId, r.DivCode, r.Year1, r.Month1, r.Week,
                (object?)r.SalesQty ?? DBNull.Value, (object?)r.SalesAmt ?? DBNull.Value,
                (object?)r.Turns ?? DBNull.Value);
        return table;
    }

    public async Task<int> UpsertRowsAsync(string country, IReadOnlyList<WeeklySalesGcpRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                CreateStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "#Staging",
                BulkCopyTimeout = CommandTimeoutSeconds,
            })
            {
                await bulk.WriteToServerAsync(ToStagingTable(rows), ct);
            }

            await c.ExecuteAsync(new CommandDefinition(
                DeleteRecentWeeksSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                InsertFromStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" — fetches the current + last week of the
    /// BigQuery feed and replaces those weeks in the given country's on-prem DB.</summary>
    public async Task<int> RefreshCountryAsync(string country, CancellationToken ct = default)
    {
        var rows = await FetchFromBigQueryAsync(ct);
        return await UpsertRowsAsync(country, rows, ct);
    }

}
