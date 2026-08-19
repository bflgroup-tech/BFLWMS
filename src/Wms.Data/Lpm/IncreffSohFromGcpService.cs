using System.Data;
using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Wms.Data.Configuration;
using Wms.Data.Gcp;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record IncreffSohGcpRow(string Country, string ItemCode, int? Soh);

/// <summary>
/// On-demand pull of ECOM stock-on-hand from BigQuery
/// (mvp-data-bi.Ecom_Bronze.INCREFF_{Country}_SOH, one source table per country)
/// for UAE and KSA, and a full-overwrite refresh of dbo.LPM_ECOM_INCREFF_SOH on
/// LPMSIM (on-prem) — a bare heap table (no PK/indexes) someone had already
/// pre-created with columns Country, Itemcode, SOH, CreateTS. Refresh deletes
/// each country's existing rows and bulk-inserts the fresh set — current-SOH
/// semantics only, no history kept.
///
/// No timer yet — triggered only from the Nightly Batches admin page's
/// "Refresh Now" button. Job-run log reuses the shared dbo.WmsRptJobRun via
/// ScheduledJobService, scoped by JobName, same as OtsWeeklyService.
/// </summary>
public class IncreffSohFromGcpService(
    IOnPremConnectionResolver resolver, IOptions<GcpBigQueryOptions> gcpOpts, IConfiguration configuration)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "IncreffSohFromGCP";

    private static readonly string[] Countries = ["UAE", "KSA"];

    // "Client Sku ID" is the source column's real (spaced) name — needs backticks
    // in BigQuery Standard SQL. Table name carries the country so each country
    // reads its own INCREFF_{country}_SOH table.
    private static string SourceQuery(string country) => $@"
        SELECT `Client Sku ID` AS ItemCode, SUM(quantity) AS Soh
          FROM Ecom_Bronze.INCREFF_{country}_SOH
         WHERE CalenderDate = @date
         GROUP BY 1";

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

    // ====================== BigQuery fetch ======================

    // BigQuery's client library returns NUMERIC/BIGNUMERIC columns as a
    // BigQueryNumeric struct, which doesn't implement IConvertible — route
    // through ToString() + Parse instead (same as WeeklySalesFromGcpService).
    private static int? ParseInt(object? value) =>
        value is null ? null : (int)decimal.Parse(value.ToString()!, CultureInfo.InvariantCulture);

    /// <summary>Pulls SOH for every configured country for the given date.</summary>
    public async Task<List<IncreffSohGcpRow>> FetchFromBigQueryAsync(DateOnly date, CancellationToken ct = default)
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

        var bqDate = date.ToDateTime(TimeOnly.MinValue);
        var rows = new List<IncreffSohGcpRow>();
        foreach (var country in Countries)
        {
            var parameters = new[] { new BigQueryParameter("date", BigQueryDbType.Date, bqDate) };
            var result = await client.ExecuteQueryAsync(SourceQuery(country), parameters, cancellationToken: ct);
            foreach (var row in result)
            {
                rows.Add(new IncreffSohGcpRow(
                    Country: country,
                    ItemCode: row["ItemCode"]?.ToString() ?? "",
                    Soh: ParseInt(row["Soh"])));
            }
        }
        return rows;
    }

    // ====================== Refresh LPM_ECOM_INCREFF_SOH (latest wins) ======================
    // No PK/unique index on this table, so "latest wins" is enforced in code:
    // delete each country's existing rows, then bulk-insert the fresh set, all
    // inside one transaction so a mid-run failure leaves the prior data intact.

    private static DataTable ToTable(IReadOnlyList<IncreffSohGcpRow> rows, DateTime createTs)
    {
        var table = new DataTable();
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("Itemcode", typeof(string));
        table.Columns.Add("SOH", typeof(int));
        table.Columns.Add("CreateTS", typeof(DateTime));
        foreach (var r in rows)
            table.Rows.Add(r.Country, r.ItemCode, (object?)r.Soh ?? DBNull.Value, createTs);
        return table;
    }

    public async Task<int> UpsertRowsAsync(IReadOnlyList<IncreffSohGcpRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var nowGst = DateTime.UtcNow.AddHours(4);

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var country in rows.Select(r => r.Country).Distinct())
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM dbo.LPM_ECOM_INCREFF_SOH WHERE Country = @country;",
                    new { country }, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.LPM_ECOM_INCREFF_SOH",
                BulkCopyTimeout = CommandTimeoutSeconds,
            })
            {
                bulk.ColumnMappings.Add("Country", "Country");
                bulk.ColumnMappings.Add("Itemcode", "Itemcode");
                bulk.ColumnMappings.Add("SOH", "SOH");
                bulk.ColumnMappings.Add("CreateTS", "CreateTS");
                await bulk.WriteToServerAsync(ToTable(rows, nowGst), ct);
            }

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" — pulls yesterday's (GST) SOH from BigQuery
    /// for every configured country and overwrites dbo.LPM_ECOM_INCREFF_SOH.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4).AddDays(-1));
        var rows = await FetchFromBigQueryAsync(yesterday, ct);
        return await UpsertRowsAsync(rows, ct);
    }
}
