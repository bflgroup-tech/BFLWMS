using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every hour to enqueue new GINs and push unsent products/GINs via
/// ApiGinIntegrationService. Gated by the dbo.WmsRptCountryConfig toggle
/// (JobName='APIGinIntegration', Country='') on the Nightly Batches admin page —
/// same convention as GenerateEan13BatchService.
/// </summary>
public class ApiGinIntegrationBatchService(IServiceProvider sp, ILogger<ApiGinIntegrationBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("ApiGinIntegrationBatchService started. Fires every hour when the job toggle is active.");

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
                log.LogError(ex, "ApiGinIntegrationBatchService: loop error; retrying in 5 min.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();

        if (!await jobs.IsActiveAsync(ApiGinIntegrationService.JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogDebug("APIGinIntegration: job toggle is inactive — skipping this tick.");
            return;
        }

        var svc = scope.ServiceProvider.GetRequiredService<ApiGinIntegrationService>();
        var (sent, failed, err) = await svc.SendPendingAsync("Interval", "Timer", ct);
        if (err is null)
            log.LogInformation("ApiGinIntegrationBatchService: {Sent} sent.", sent);
        else
            log.LogError("ApiGinIntegrationBatchService: {Failed} failed — {Error}", failed, err);
    }
}
