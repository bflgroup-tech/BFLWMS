using System.Data;
using System.Globalization;
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

public record WhStockLastDayGcpRow(
    string Country, string Warehouse, string PalletCategory, DateTime LastDayOfMonth,
    string Division, string Season, int? Qty, int? SkuCount, int? BoxCount, int? PalletCount,
    DateTime? CreatedTs);

/// <summary>
/// Pulls the monthly warehouse-stock-last-day snapshot from BigQuery
/// (mvp-data-bi.cdm_silver.wh_stock_last_day) and MERGE-upserts it into LPMSIM's
/// dbo.WMS_WHSTOCK_LASTDAY. Unlike WeeklySalesFromGcpService's source, this one
/// already carries its own Country column, so the full feed is fetched once and
/// then filtered per active country before each country's upsert -- "active"
/// here controls which countries' rows get written, not which query runs.
///
/// Country activation + job-run log reuse dbo.WmsRptCountryConfig / dbo.WmsRptJobRun
/// on the Azure WMS DB, scoped by JobName, same pattern as WeeklySalesFromGcpService.
///
/// NOTE: the filter match is `Country` (case-insensitive) against BigQuery's own
/// Country string values -- these haven't been sampled against WMS's country
/// naming (UAE/KSA/Bahrain/...) yet. If a country shows 0 rows upserted despite
/// being active and the feed clearly containing that country's data, the BigQuery
/// value likely doesn't match the WMS country name exactly -- check via "Refresh
/// Now" before relying on the weekly timer.
/// </summary>
public class WhStockLastDayFromGcpService(IOnPremConnectionResolver resolver, IOptions<GcpBigQueryOptions> gcpOpts, IConfiguration configuration, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "WhStockLastDayFromGCP";

    private const string SourceQuery = @"
        SELECT Country, Warehouse, PalletCategory, LastDayOfMonth, Division, Season,
               Qty, SKUCount, BoxCount, PalletCount, Created_ts
          FROM cdm_silver.wh_stock_last_day";

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
    // BigQueryNumeric struct, which doesn't implement IConvertible — route
    // through ToString() + Parse instead (same as WeeklySalesFromGcpService).
    private static int? ParseInt(object? value) =>
        value is null ? null : (int)decimal.Parse(value.ToString()!, CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(object? value) =>
        value is null ? null : DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture);

    /// <summary>Pulls the full feed (every country) from BigQuery — the source table
    /// is a small monthly snapshot, not per-day volume, so a full pull each run is
    /// cheap. Filtering to active countries happens at upsert time, not here.</summary>
    public async Task<List<WhStockLastDayGcpRow>> FetchFromBigQueryAsync(CancellationToken ct = default)
    {
        var opts = gcpOpts.Value;
        var projectId = configuration["GCP_PROJECT_ID"];
        if (string.IsNullOrWhiteSpace(projectId)) projectId = opts.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(
                "BigQuery is not configured — set GCP_PROJECT_ID (or BigQuery:ProjectId) in configuration.");

        var serviceAccountJson = configuration["GCP_SERVICE_ACCOUNT_JSON"];
        var client = !string.IsNullOrWhiteSpace(serviceAccountJson)
            ? await BigQueryClient.CreateAsync(projectId, GoogleCredential.FromJson(serviceAccountJson))
            : !string.IsNullOrWhiteSpace(opts.CredentialsPath)
                ? await BigQueryClient.CreateAsync(projectId, GoogleCredential.FromFile(opts.CredentialsPath))
                : await BigQueryClient.CreateAsync(projectId);

        var result = await client.ExecuteQueryAsync(SourceQuery, parameters: null, cancellationToken: ct);

        var rows = new List<WhStockLastDayGcpRow>();
        foreach (var row in result)
        {
            rows.Add(new WhStockLastDayGcpRow(
                Country:        row["Country"]?.ToString() ?? "",
                Warehouse:      row["Warehouse"]?.ToString() ?? "",
                PalletCategory: row["PalletCategory"]?.ToString() ?? "",
                LastDayOfMonth: ParseDate(row["LastDayOfMonth"]) ?? default,
                Division:       row["Division"]?.ToString() ?? "",
                Season:         row["Season"]?.ToString() ?? "",
                Qty:            ParseInt(row["Qty"]),
                SkuCount:       ParseInt(row["SKUCount"]),
                BoxCount:       ParseInt(row["BoxCount"]),
                PalletCount:    ParseInt(row["PalletCount"]),
                CreatedTs:      ParseDate(row["Created_ts"])));
        }
        return rows;
    }

    // ====================== Upsert into dbo.WMS_WHSTOCK_LASTDAY (one country's rows) ======================
    // Bulk-copies the country's slice of the already-fetched feed into a session-scoped
    // #Staging temp table, then a single set-based MERGE — same shape as
    // WeeklySalesFromGcpService, just filtered to one country's rows first.

    private const string CreateStagingSql = @"
        CREATE TABLE #Staging (
            Country        NVARCHAR(50)  NOT NULL,
            Warehouse      NVARCHAR(50)  NOT NULL,
            PalletCategory NVARCHAR(50)  NOT NULL,
            LastDayOfMonth DATE          NOT NULL,
            Division       NVARCHAR(100) NOT NULL,
            Season         NVARCHAR(20)  NOT NULL,
            Qty            INT           NULL,
            SKUCount       INT           NULL,
            BoxCount       INT           NULL,
            PalletCount    INT           NULL,
            Created_ts     DATE          NULL
        );";

    private const string MergeFromStagingSql = @"
        MERGE dbo.WMS_WHSTOCK_LASTDAY AS t
        USING #Staging AS s
          ON t.Country = s.Country AND t.Warehouse = s.Warehouse AND t.PalletCategory = s.PalletCategory
         AND t.LastDayOfMonth = s.LastDayOfMonth AND t.Division = s.Division AND t.Season = s.Season
        WHEN MATCHED THEN
          UPDATE SET Qty = s.Qty, SKUCount = s.SKUCount, BoxCount = s.BoxCount, PalletCount = s.PalletCount,
                     Created_ts = s.Created_ts, UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
        WHEN NOT MATCHED THEN
          INSERT (Country, Warehouse, PalletCategory, LastDayOfMonth, Division, Season, Qty, SKUCount, BoxCount, PalletCount, Created_ts)
          VALUES (s.Country, s.Warehouse, s.PalletCategory, s.LastDayOfMonth, s.Division, s.Season, s.Qty, s.SKUCount, s.BoxCount, s.PalletCount, s.Created_ts);";

    private static DataTable ToStagingTable(IReadOnlyList<WhStockLastDayGcpRow> rows)
    {
        var table = new DataTable();
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("Warehouse", typeof(string));
        table.Columns.Add("PalletCategory", typeof(string));
        table.Columns.Add("LastDayOfMonth", typeof(DateTime));
        table.Columns.Add("Division", typeof(string));
        table.Columns.Add("Season", typeof(string));
        table.Columns.Add("Qty", typeof(int));
        table.Columns.Add("SKUCount", typeof(int));
        table.Columns.Add("BoxCount", typeof(int));
        table.Columns.Add("PalletCount", typeof(int));
        table.Columns.Add("Created_ts", typeof(DateTime));
        foreach (var r in rows)
            table.Rows.Add(
                r.Country, r.Warehouse, r.PalletCategory, r.LastDayOfMonth, r.Division, r.Season,
                (object?)r.Qty ?? DBNull.Value, (object?)r.SkuCount ?? DBNull.Value,
                (object?)r.BoxCount ?? DBNull.Value, (object?)r.PalletCount ?? DBNull.Value,
                (object?)r.CreatedTs ?? DBNull.Value);
        return table;
    }

    /// <summary>Upserts one country's slice of the already-fetched feed. Returns how many
    /// of that country's rows were found in the feed (0 is a valid, real answer — see the
    /// class-level note about BigQuery's Country values not yet being confirmed to match
    /// WMS country names).</summary>
    public async Task<int> UpsertRowsAsync(string country, IReadOnlyList<WhStockLastDayGcpRow> allRows, CancellationToken ct = default)
    {
        var rows = allRows.Where(r => string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase)).ToList();
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
                MergeFromStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" for one country — fetches the full BigQuery feed
    /// and upserts just that country's slice.</summary>
    public async Task<int> RefreshCountryAsync(string country, CancellationToken ct = default)
    {
        var rows = await FetchFromBigQueryAsync(ct);
        return await UpsertRowsAsync(country, rows, ct);
    }
}
