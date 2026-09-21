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

public record IncreffItemLevelSohGcpRow(
    string Country, string ItemCode, string? ItemType, string? BinStatus, int? Quantity, DateOnly CalenderDate);

/// <summary>
/// On-demand pull of ECOM stock-on-hand from a newer BigQuery source
/// (mvp-data-bi.Ecom_Silver.Increff_Item_Level_SOH) — a single combined table
/// across every channel/country, unlike IncreffSohFromGcpService's per-country
/// Bronze-tier tables (Ecom_Bronze.INCREFF_{Country}_SOH). This Silver-tier
/// table also carries Item_Type/Bin Status granularity the Bronze tables
/// don't, so the destination keeps one row per (Country, Itemcode, ItemType,
/// BinStatus) rather than collapsing straight to a single SOH per item.
///
/// Writes to dbo.LPM_ECOM_INCREFF_SOH_NEW on LPMSIM (on-prem), a SEPARATE
/// table from dbo.LPM_ECOM_INCREFF_SOH — deliberately parallel/validation
/// only. The live ECOM Stock Variance Report (EcomStockVarianceReportService /
/// IncreffMfcsSohCompareService) still reads dbo.LPM_ECOM_INCREFF_SOH, so this
/// job cannot affect it. "Channel" in the source is NOT the Country code —
/// observed values are 'BFL' (UAE) and 'BFL-KSA' (KSA), mapped in SourceQuery.
/// An unrecognized Channel value passes through as-is (not silently dropped)
/// so it stays visible for investigation rather than disappearing.
///
/// Refresh is TRUNCATE + bulk-insert of the WHOLE table every run (unlike
/// IncreffSohFromGcpService's per-country delete — this table has no country
/// split to preserve, per user request), all inside one transaction so a
/// mid-run failure leaves the prior snapshot intact.
///
/// Fires daily via IncreffSohFromGcpNewBatchService (Hosting/), same pattern
/// as IncreffSohFromGcpBatchService but offset to 08:05 GST so it doesn't
/// contend with the existing 08:00/08:15 GST pipeline while still pulling a
/// comparable "yesterday GST" snapshot for validation against it.
/// </summary>
public class IncreffSohFromGcpNewService(
    IOnPremConnectionResolver resolver, IOptions<GcpBigQueryOptions> gcpOpts, IConfiguration configuration)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "IncreffSohFromGCP_New";

    // Item_Code falls back to `Client Sku ID` when blank — same fallback the
    // user specified for this source. Backticked identifiers/IFNULL are
    // BigQuery Standard SQL, not on-prem SQL Server syntax. Grouped (not a
    // plain SELECT) because the source is row-per-bin — summing Quantity
    // within each (Channel, ItemCode, Item_Type, Bin Status, CalenderDate)
    // combination is what turns bin-level rows into the SOH figure per that
    // combination.
    private const string SourceQuery = @"
        SELECT
            CASE
                WHEN Channel = 'BFL' THEN 'UAE'
                WHEN Channel = 'BFL-KSA' THEN 'KSA'
                ELSE Channel
            END AS Country,
            CASE
                WHEN IFNULL(Item_Code, '') = '' THEN `Client Sku ID`
                ELSE Item_Code
            END AS ItemCode,
            Item_Type,
            `Bin Status` AS BinStatus,
            SUM(Quantity) AS Quantity,
            CalenderDate
          FROM `Ecom_Silver.Increff_Item_Level_SOH`
         WHERE CalenderDate = @date
         GROUP BY Channel, ItemCode, Item_Type, BinStatus, CalenderDate";

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
    // through ToString() + Parse instead (same as IncreffSohFromGcpService).
    private static int? ParseInt(object? value) =>
        value is null ? null : (int)decimal.Parse(value.ToString()!, CultureInfo.InvariantCulture);

    /// <summary>Pulls SOH for every channel/country for the given date in one query
    /// (the source table isn't split per country, unlike the old Bronze-tier tables).</summary>
    public async Task<List<IncreffItemLevelSohGcpRow>> FetchFromBigQueryAsync(DateOnly date, CancellationToken ct = default)
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
        var parameters = new[] { new BigQueryParameter("date", BigQueryDbType.Date, bqDate) };
        var result = await client.ExecuteQueryAsync(SourceQuery, parameters, cancellationToken: ct);

        var rows = new List<IncreffItemLevelSohGcpRow>();
        foreach (var row in result)
        {
            rows.Add(new IncreffItemLevelSohGcpRow(
                Country: row["Country"]?.ToString() ?? "",
                ItemCode: row["ItemCode"]?.ToString() ?? "",
                ItemType: row["Item_Type"]?.ToString(),
                BinStatus: row["BinStatus"]?.ToString(),
                Quantity: ParseInt(row["Quantity"]),
                CalenderDate: date));
        }
        return rows;
    }

    // ====================== Refresh LPM_ECOM_INCREFF_SOH_NEW (full snapshot) ======================
    // TRUNCATE + bulk-insert of the WHOLE table every run, all inside one
    // transaction so a mid-run failure leaves the prior snapshot intact.

    private static DataTable ToTable(IReadOnlyList<IncreffItemLevelSohGcpRow> rows, DateTime createTs)
    {
        var table = new DataTable();
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("Itemcode", typeof(string));
        table.Columns.Add("ItemType", typeof(string));
        table.Columns.Add("BinStatus", typeof(string));
        table.Columns.Add("Quantity", typeof(int));
        table.Columns.Add("CalenderDate", typeof(DateTime));
        table.Columns.Add("CreateTS", typeof(DateTime));
        foreach (var r in rows)
            table.Rows.Add(
                r.Country, r.ItemCode, (object?)r.ItemType ?? DBNull.Value, (object?)r.BinStatus ?? DBNull.Value,
                (object?)r.Quantity ?? DBNull.Value, r.CalenderDate.ToDateTime(TimeOnly.MinValue), createTs);
        return table;
    }

    public async Task<int> UpsertRowsAsync(IReadOnlyList<IncreffItemLevelSohGcpRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var nowGst = DateTime.UtcNow.AddHours(4);

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                "TRUNCATE TABLE dbo.LPM_ECOM_INCREFF_SOH_NEW;",
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = "dbo.LPM_ECOM_INCREFF_SOH_NEW",
                BulkCopyTimeout = CommandTimeoutSeconds,
            })
            {
                bulk.ColumnMappings.Add("Country", "Country");
                bulk.ColumnMappings.Add("Itemcode", "Itemcode");
                bulk.ColumnMappings.Add("ItemType", "ItemType");
                bulk.ColumnMappings.Add("BinStatus", "BinStatus");
                bulk.ColumnMappings.Add("Quantity", "Quantity");
                bulk.ColumnMappings.Add("CalenderDate", "CalenderDate");
                bulk.ColumnMappings.Add("CreateTS", "CreateTS");
                await bulk.WriteToServerAsync(ToTable(rows, nowGst), ct);
            }

            await tx.CommitAsync(ct);
            return rows.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    /// <summary>On-demand "Refresh Now" — pulls yesterday's (GST) SOH from BigQuery
    /// for every channel/country and overwrites dbo.LPM_ECOM_INCREFF_SOH_NEW.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4).AddDays(-1));
        var rows = await FetchFromBigQueryAsync(yesterday, ct);
        return await UpsertRowsAsync(rows, ct);
    }
}
