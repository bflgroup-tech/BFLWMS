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

public record WhStockLastDayGcpRow(
    string Country, string Warehouse, string PalletCategory, DateTime LastDayOfMonth,
    string Division, string Season, int? Qty, int? SkuCount, int? BoxCount, int? PalletCount,
    DateTime? CreatedTs);

/// <summary>
/// Pulls the monthly warehouse-stock-last-day snapshot from BigQuery
/// (mvp-data-bi.cdm_silver.wh_stock_last_day) and replaces it into LPMSIM's
/// dbo.WMS_WHSTOCK_LASTDAY, scoped to (Country, LastDayOfMonth): for each active
/// country, every existing row for the exact LastDayOfMonth values present in
/// that country's fetched slice is deleted, then the fresh slice is inserted in
/// full. This -- not a MERGE -- is deliberate: a MERGE's "WHEN MATCHED THEN
/// UPDATE" never removes a (Division, Season, PalletCategory, Warehouse) combo
/// that used to exist for a given country/month but has since dropped out of the
/// BigQuery feed, so stale combos would accumulate forever. Delete-then-insert
/// scoped to the touched months means other months/countries not part of this
/// run are left untouched, unlike a blind TRUNCATE TABLE of the whole table.
///
/// Unlike WeeklySalesFromGcpService's source, this one already carries its own
/// Country column -- rather than a per-country "add country" activation step
/// (which just meant clicking Add six times for six countries the feed already
/// has), this job is a single Active toggle: the full feed is fetched once and
/// every distinct country found in it gets replaced, same single-row shape
/// WeeklySalesFromGCP already uses on Nightly Batches Status.
///
/// Job-run log and the cross-instance lock live in the shared ScheduledJobService
/// (see WhStockLastDayBatchService) rather than a duplicated copy here -- this
/// class only knows how to fetch and replace.
/// </summary>
public class WhStockLastDayFromGcpService(IOnPremConnectionResolver resolver, IOptions<GcpBigQueryOptions> gcpOpts, IConfiguration configuration)
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

    // ====================== Replace one country's rows in dbo.WMS_WHSTOCK_LASTDAY ======================
    // Bulk-copies the country's slice of the already-fetched feed into a session-scoped
    // #Staging temp table, then DELETEs every existing row for this country at exactly
    // the LastDayOfMonth values present in that slice (clearing out any combo that's
    // dropped out of the feed since the last run) before INSERTing the fresh slice in
    // full. Scoped to (Country, LastDayOfMonth) so other months/countries untouched by
    // this run are left alone -- not a MERGE (never deletes stale combos) and not a
    // blind TRUNCATE TABLE (would wipe every month/country, not just this run's).

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

    // #Staging holds only this one country's rows (filtered before bulk copy), so the
    // subquery needs no Country filter of its own.
    private const string DeleteExistingScopeSql = @"
        DELETE FROM dbo.WMS_WHSTOCK_LASTDAY
         WHERE Country = @country
           AND LastDayOfMonth IN (SELECT DISTINCT LastDayOfMonth FROM #Staging);";

    private const string InsertFromStagingSql = @"
        INSERT INTO dbo.WMS_WHSTOCK_LASTDAY
            (Country, Warehouse, PalletCategory, LastDayOfMonth, Division, Season, Qty, SKUCount, BoxCount, PalletCount, Created_ts)
        SELECT Country, Warehouse, PalletCategory, LastDayOfMonth, Division, Season, Qty, SKUCount, BoxCount, PalletCount, Created_ts
          FROM #Staging;";

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

    /// <summary>Replaces one country's slice of the already-fetched feed: deletes every
    /// existing row for this country at the LastDayOfMonth values present in the fetch,
    /// then inserts the fresh slice in full (see the class-level note on why this isn't a
    /// MERGE or a table-wide TRUNCATE). Returns how many of that country's rows were found
    /// in the feed (0 is a valid, real answer — see the class-level note about BigQuery's
    /// Country values not yet being confirmed to match WMS country names).</summary>
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
                DeleteExistingScopeSql, new { country }, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await c.ExecuteAsync(new CommandDefinition(
                InsertFromStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" — fetches the full BigQuery feed once and upserts
    /// every distinct country found in it. There's no per-country activation for this job
    /// (single Active toggle, like WeeklySalesFromGCP) -- whatever countries the source
    /// carries get upserted, no "add country" step needed.</summary>
    public async Task<List<(string Country, int Rows)>> RefreshAllCountriesAsync(CancellationToken ct = default)
    {
        var rows = await FetchFromBigQueryAsync(ct);
        var countries = rows.Select(r => r.Country)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<(string Country, int Rows)>();
        foreach (var country in countries)
            result.Add((country, await UpsertRowsAsync(country, rows, ct)));
        return result;
    }
}
