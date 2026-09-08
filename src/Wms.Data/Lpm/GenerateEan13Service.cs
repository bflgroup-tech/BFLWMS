using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Core;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Backfills DATAREPORTING.dbo.UPC_SUBCLASS.EAN13 for rows that carry an
/// ORACLE_SKU but no EAN13 yet. On-demand only ("Generate Now" on the Nightly
/// Batches admin page) — no timer.
///
/// EAN13 = "200" + ORACLE_SKU left-padded to 9 digits + a computed check
/// digit. GS1's 200-299 prefix range is reserved for internal/restricted-
/// circulation use, which is what turns an internal Oracle SKU into a
/// scannable EAN13 here. The check digit is computed via Ean13Barcode.Normalize
/// (Wms.Core) — the same helper the Robo Sorting print tickets use. A SKU with
/// more than 9 digits is skipped rather than truncated: truncating would
/// silently generate a barcode for a DIFFERENT sku.
/// </summary>
public class GenerateEan13Service(IOnPremConnectionResolver resolver, ScheduledJobService jobs)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "GenerateEAN13";

    // WmsProductionDb, not OnPremBackup — the OnPremBackup login has no UPDATE
    // grant on DATAREPORTING.dbo.UPC_SUBCLASS (same class of issue documented on
    // ContainerAllocationDataSyncService's PhotoCheckingResult/RFIDTransfer
    // writes, which use WmsProductionDb for the same reason).
    private SqlConnection OpenWmsProductionDb()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetWmsProductionDbConnectionString()) { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private const string SelectSql = @"
        SELECT ORACLE_SKU
          FROM DATAREPORTING.dbo.UPC_SUBCLASS
         WHERE ISNULL(ORACLE_SKU, '') <> '' AND ISNULL(EAN13, '') = '';";

    private const string CreateStagingSql = @"
        CREATE TABLE #Ean13Updates (ORACLE_SKU NVARCHAR(50) NOT NULL PRIMARY KEY, EAN13 CHAR(13) NOT NULL);";

    private const string UpdateFromStagingSql = @"
        UPDATE us
           SET us.EAN13 = u.EAN13
          FROM DATAREPORTING.dbo.UPC_SUBCLASS us
         INNER JOIN #Ean13Updates u ON u.ORACLE_SKU = us.ORACLE_SKU;";

    /// <summary>Turns an ORACLE_SKU into its EAN13, or null when the SKU has no
    /// digits or more than 9 of them (would not fit the 200-prefix scheme).</summary>
    internal static string? ToEan13(string oracleSku)
    {
        var digits = new string(oracleSku.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || digits.Length > 9) return null;
        return Ean13Barcode.Normalize("200" + digits.PadLeft(9, '0'));
    }

    private static DataTable ToStagingTable(IEnumerable<(string Sku, string Ean13)> rows)
    {
        var t = new DataTable();
        t.Columns.Add("ORACLE_SKU", typeof(string));
        t.Columns.Add("EAN13", typeof(string));
        foreach (var (sku, ean13) in rows) t.Rows.Add(sku, ean13);
        return t;
    }

    /// <summary>
    /// Generates and writes EAN13 for every eligible UPC_SUBCLASS row, and
    /// writes one dbo.WmsRptJobRun row. Never throws — the outcome is in the
    /// returned tuple and in the run log, so a caller does not need its own
    /// try/catch.
    /// </summary>
    public async Task<(int Rows, string? Error)> RunOnceAsync(string mode, string triggeredBy, CancellationToken ct = default)
    {
        // Cross-instance guard, same convention as LpmSalesTurnsRefreshService /
        // OtsWeeklyService — harmless here too, and cheap insurance if a timer is
        // ever added later.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate work.", ct);
            return (0, null);
        }

        var runId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        try
        {
            await using var c = OpenWmsProductionDb();

            var skus = (await c.QueryAsync<string>(new CommandDefinition(
                SelectSql, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var updates = skus
                .Select(sku => (Sku: sku, Ean13: ToEan13(sku)))
                .Where(x => x.Ean13 is not null)
                .Select(x => (x.Sku, Ean13: x.Ean13!))
                .ToList();

            if (updates.Count == 0)
            {
                await jobs.FinishRunAsync(runId, "Success", 0, null, ct);
                return (0, null);
            }

            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            try
            {
                await c.ExecuteAsync(new CommandDefinition(
                    CreateStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = "#Ean13Updates",
                    BulkCopyTimeout = CommandTimeoutSeconds,
                })
                {
                    bulk.ColumnMappings.Add("ORACLE_SKU", "ORACLE_SKU");
                    bulk.ColumnMappings.Add("EAN13", "EAN13");
                    await bulk.WriteToServerAsync(ToStagingTable(updates), ct);
                }

                var rows = await c.ExecuteAsync(new CommandDefinition(
                    UpdateFromStagingSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                await tx.CommitAsync(ct);

                await jobs.FinishRunAsync(runId, "Success", rows, null, ct);
                return (rows, null);
            }
            catch { await tx.RollbackAsync(ct); throw; }
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            return (0, ex.Message);
        }
    }
}
