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

public record WeeklySalesGcpRow(string StoreId, int DivCode, int Year1, int Month1, int Week, int? SalesQty, decimal? SalesAmt);

/// <summary>
/// Pulls the full weekly sales feed from BigQuery (mvp-data-bi.cdm_silver.it_sales_qty)
/// and MERGE-upserts it into dbo.LPM_Weekly_SalesAmt — the same table
/// OtsPoAllocationService's "Generate Volume Group" reads for weighted monthly sales.
/// The source has no country column, so the same BigQuery result set is written for
/// every active country.
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

    private const string SourceQuery = @"
        SELECT storeid, DivCode, CalendarYear, CalendarMonth, CalendarWeek, Soldqty, NetSalesExVAT
          FROM cdm_silver.it_sales_qty";

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

    public async Task<List<WeeklySalesGcpRow>> FetchFromBigQueryAsync(CancellationToken ct = default)
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

        var rows = new List<WeeklySalesGcpRow>();
        foreach (var row in result)
        {
            rows.Add(new WeeklySalesGcpRow(
                StoreId:  Convert.ToString(row["storeid"]) ?? "",
                DivCode:  Convert.ToInt32(row["DivCode"]),
                Year1:    Convert.ToInt32(row["CalendarYear"]),
                Month1:   Convert.ToInt32(row["CalendarMonth"]),
                Week:     Convert.ToInt32(row["CalendarWeek"]),
                SalesQty: row["Soldqty"]       is null ? null : Convert.ToInt32(row["Soldqty"]),
                SalesAmt: row["NetSalesExVAT"] is null ? null : Convert.ToDecimal(row["NetSalesExVAT"])));
        }
        return rows;
    }

    // ====================== Upsert into one country's on-prem LPM_Weekly_SalesAmt ======================

    private const string UpsertSql = @"
        MERGE dbo.LPM_Weekly_SalesAmt AS t
        USING (SELECT @StoreId AS StoreID, @DivCode AS DivCode, @Year1 AS Year1, @Month1 AS Month1, @Week AS Week) AS s
          ON t.StoreID = s.StoreID AND t.DivCode = s.DivCode AND t.Year1 = s.Year1 AND t.Month1 = s.Month1 AND t.Week = s.Week
        WHEN MATCHED THEN
          UPDATE SET SalesQty = @SalesQty, SalesAmt = @SalesAmt, UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
        WHEN NOT MATCHED THEN
          INSERT (StoreID, DivCode, Year1, Month1, Week, SalesQty, SalesAmt, CreateTS)
          VALUES (@StoreId, @DivCode, @Year1, @Month1, @Week, @SalesQty, @SalesAmt, DATEADD(hour, 4, SYSUTCDATETIME()));";

    public async Task<int> UpsertRowsAsync(string country, IReadOnlyList<WeeklySalesGcpRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                UpsertSql, rows, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" — fetches the full BigQuery feed and upserts it
    /// into the given country's on-prem DB.</summary>
    public async Task<int> RefreshCountryAsync(string country, CancellationToken ct = default)
    {
        var rows = await FetchFromBigQueryAsync(ct);
        return await UpsertRowsAsync(country, rows, ct);
    }

    // ====================== Admin "Run migration" buttons (mirror db/*.sql — idempotent) ======================

    private const string JobNameMigrationSql = @"
        IF NOT EXISTS (
            SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
              AND name = 'JobName'
        )
        BEGIN
            ALTER TABLE dbo.WmsRptCountryConfig
                ADD JobName NVARCHAR(100) NULL;

            UPDATE dbo.WmsRptCountryConfig SET JobName = 'MissingExcessSnapshot' WHERE JobName IS NULL;

            ALTER TABLE dbo.WmsRptCountryConfig
                ALTER COLUMN JobName NVARCHAR(100) NOT NULL;

            DECLARE @pk NVARCHAR(200) = (
                SELECT kc.name FROM sys.key_constraints kc
                WHERE kc.parent_object_id = OBJECT_ID('dbo.WmsRptCountryConfig')
                  AND kc.type = 'PK');
            DECLARE @sql NVARCHAR(400) = N'ALTER TABLE dbo.WmsRptCountryConfig DROP CONSTRAINT ' + QUOTENAME(@pk) + N';';
            EXEC sp_executesql @sql;

            ALTER TABLE dbo.WmsRptCountryConfig
                ADD CONSTRAINT PK_WmsRptCountryConfig PRIMARY KEY (JobName, Country);
        END;";

    private const string SalesQtyMigrationSql = @"
        IF NOT EXISTS (
            SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.LPM_Weekly_SalesAmt')
              AND name = 'SalesQty'
        )
        BEGIN
            ALTER TABLE dbo.LPM_Weekly_SalesAmt
                ADD SalesQty INT NULL;
        END;";

    /// <summary>Runs db/migrate_wms_rpt_countryconfig_add_jobname.sql against the Azure WMS DB.
    /// Idempotent — safe to click more than once.</summary>
    public async Task RunJobNameMigrationAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(
            JobNameMigrationSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>Runs db/migrate_lpm_weekly_salesamt_add_salesqty.sql against the given
    /// country's on-prem DB. Idempotent — safe to click more than once.</summary>
    public async Task RunSalesQtyMigrationAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            SalesQtyMigrationSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }
}
