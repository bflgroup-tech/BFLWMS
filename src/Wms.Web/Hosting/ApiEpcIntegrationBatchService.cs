using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every hour to push not-yet-sent EPCBarcodes rows via ApiEpcIntegrationService.
/// Gated by the dbo.WmsRptCountryConfig toggle (JobName='APIEpcIntegration',
/// Country='') on the Nightly Batches admin page — same convention as
/// GenerateEan13BatchService.
/// </summary>
public class ApiEpcIntegrationBatchService(IServiceProvider sp, ILogger<ApiEpcIntegrationBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("ApiEpcIntegrationBatchService started. Fires every hour when the job toggle is active.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "ApiEpcIntegrationBatchService: loop error; retrying in 5 min.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();

        if (!await jobs.IsActiveAsync(ApiEpcIntegrationService.JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogDebug("APIEpcIntegration: job toggle is inactive — skipping this tick.");
            return;
        }

        var svc = scope.ServiceProvider.GetRequiredService<ApiEpcIntegrationService>();
        var (rows, err) = await svc.SendPendingEpcAsync("Interval", "Timer", ct);
        if (err is null)
            log.LogInformation("ApiEpcIntegrationBatchService: {Rows} EPC items sent.", rows);
        else
            log.LogError("ApiEpcIntegrationBatchService: FAILED — {Error}", err);
    }
}
