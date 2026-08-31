using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Refreshes dbo.LPM_SalesTurns (on LPMSIM, on-prem) for the current and previous
/// GST month/year from dbo.LPM_Weekly_SalesAmt — delete-then-insert a fresh
/// per-(StoreID, DivCode, Year1, Month1) aggregate, restricted to DivCodes that
/// exist in dbo.Division (drops junk/retired division codes from the aggregate).
/// Chained right after
/// WeeklySalesBatchService's Sunday 01:00 GST BigQuery pull succeeds, so it
/// aggregates the feed that just landed; also exposed as its own "Refresh Now" on
/// the Nightly Batches admin page. Job-run log reuses the shared dbo.WmsRptJobRun
/// via ScheduledJobService, single-row (Country = ""), same convention as
/// OtsWeeklyService.
/// </summary>
public class LpmSalesTurnsRefreshService(IOnPremConnectionResolver resolver, ScheduledJobService jobs)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "LpmSalesTurnsRefresh";

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString()) { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    public Task<bool> IsActiveAsync(CancellationToken ct = default) =>
        jobs.IsActiveAsync(JobName, ScheduledJobService.SingleRowKey, ct);

    // LPM_Weekly_SalesAmt is the source of truth and is left untouched — only the
    // LPM_SalesTurns aggregate is cleared and rebuilt for the two target months.
    private const string DeleteSql = @"
        DELETE FROM dbo.LPM_SalesTurns
         WHERE (Year1 = @curYear AND Month1 = @curMonth)
            OR (Year1 = @prevYear AND Month1 = @prevMonth);";

    private const string InsertSql = @"
        INSERT INTO dbo.LPM_SalesTurns
        SELECT StoreID, DivCode, Year1, Month1, SUM(SalesQty) Soldqty, 0 TurnsQty, GETDATE(), SUM(SalesAmt) SalesAmt
          FROM dbo.LPM_Weekly_SalesAmt
         WHERE ((Year1 = @curYear AND Month1 = @curMonth)
             OR (Year1 = @prevYear AND Month1 = @prevMonth))
           AND DivCode IN (SELECT DivCode FROM dbo.Division)
         GROUP BY StoreID, DivCode, Year1, Month1;";

    /// <summary>
    /// Runs the delete-then-insert refresh for the current and previous GST
    /// month/year and writes one dbo.WmsRptJobRun row. Never throws — the outcome
    /// is in the returned tuple and in the run log, so a caller does not need its
    /// own try/catch.
    /// </summary>
    public async Task<(int Rows, string? Error)> RunOnceAsync(string mode, string triggeredBy, CancellationToken ct = default)
    {
        var nowGst = DateTime.UtcNow.AddHours(4);
        var curMonth = nowGst.Month;
        var curYear = nowGst.Year;
        var prevMonth = curMonth == 1 ? 12 : curMonth - 1;
        var prevYear = curMonth == 1 ? curYear - 1 : curYear;

        // Same DELETE-then-INSERT concurrency risk as OtsWeeklyService.Generate —
        // every App Service instance runs the same HostedService, so guard against
        // two interleaved runs producing duplicate/partial rows.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate rows.", ct);
            return (0, null);
        }

        var runId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        try
        {
            var parms = new { curYear, curMonth, prevYear, prevMonth };

            await using var c = OpenOnPremBackup();
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            try
            {
                await c.ExecuteAsync(new CommandDefinition(
                    DeleteSql, parms, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                var rows = await c.ExecuteAsync(new CommandDefinition(
                    InsertSql, parms, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
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
