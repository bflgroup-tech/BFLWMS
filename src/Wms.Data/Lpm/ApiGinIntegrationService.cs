using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Pushes GIN-linked shop-issue transfers to the external API at
/// https://api.bfl.altavantconsulting.eu/v1/store-inbounds (POST, API-key auth
/// header "apikey").
///
/// STEP 1 (pending): enqueue newly-eligible GINs into bfldata.dbo.APICallGIN. The
/// INSERT itself was reverted for now — not yet run against production. Job-run
/// scaffolding (lock, log, admin "Enqueue Now" button) is in place and ready for it.
///
/// STEP 2 (pending): read queued rows from APICallGIN and POST them to the API. Not
/// implemented yet — waiting on the exact field mapping (which columns feed
/// inbound_reference, source_reference, sku, expected_quantity, pallet, parcel,
/// tracking_number, etc.) and how a row gets marked as sent.
/// </summary>
public class ApiGinIntegrationService(IOnPremConnectionResolver resolver, ScheduledJobService jobs)
{
    private const int CommandTimeoutSeconds = 60;
    public const string JobName = "APIGinIntegration";

    // WmsProductionDb, not OnPremBackup — same as GinTrailerUpdateService/
    // GenerateEan13Service/JafzaExportCheckingService/ContainerAllocationDataSyncService/
    // TechnoBuildingService for BFLDATA writes: the OnPremBackup login has been denied
    // UPDATE/INSERT there before.
    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(resolver.GetWmsProductionDbConnectionString());
        c.Open();
        return c;
    }

    /// <summary>
    /// Will enqueue newly-eligible GINs into bfldata.dbo.APICallGIN and write one
    /// dbo.WmsRptJobRun row. The actual INSERT is not wired in yet — reverted pending
    /// confirmation before it runs against production. Never throws — the outcome is
    /// in the returned tuple and in the run log, so a caller does not need its own
    /// try/catch.
    /// </summary>
    public async Task<(int Rows, string? Error)> EnqueuePendingGinsAsync(string mode, string triggeredBy, CancellationToken ct = default)
    {
        // Cross-instance guard, same convention as LpmSalesTurnsRefreshService /
        // GenerateEan13Service — kept ready for when the INSERT is wired back in.
        await using var jobLock = await jobs.TryAcquireJobLockAsync(JobName, ct);
        if (!jobLock.Acquired)
        {
            var skipId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
            await jobs.FinishRunAsync(skipId, "Skipped", 0,
                "Another instance is already running this job — skipped to avoid duplicate work.", ct);
            return (0, null);
        }

        var runId = await jobs.StartRunAsync(JobName, mode, null, triggeredBy, ct);
        const string notReady = "Not implemented yet — the enqueue query was reverted pending confirmation.";
        await jobs.FinishRunAsync(runId, "Failed", null, notReady, ct);
        return (0, notReady);
    }
}
