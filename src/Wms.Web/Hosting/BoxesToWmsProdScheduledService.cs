using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every 30 minutes to push unpublished WmsUPCBoxHead + WmsUPCBoxDet
/// rows to on-prem usa.dbo.upcboxhead + usa.dbo.upcboxdet.
///
/// GATED by config flag `Scheduler:BoxesToWmsProd:Enabled` (default false).
/// The service is registered unconditionally so the flag can be flipped at
/// runtime via App Service configuration without a redeploy — but the
/// interval-fire loop no-ops until the flag is set to true.
/// </summary>
public class BoxesToWmsProdScheduledService(
    IServiceProvider sp,
    IConfiguration cfg,
    ILogger<BoxesToWmsProdScheduledService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("BoxesToWmsProdScheduledService started. Fires every 30 min when Scheduler:BoxesToWmsProd:Enabled=true.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var enabled = cfg.GetValue("Scheduler:BoxesToWmsProd:Enabled", false);
                if (enabled)
                {
                    log.LogInformation("BoxesToWmsProd: enabled=true — firing push.");
                    await RunAsync(stoppingToken);
                }
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "BoxesToWmsProdScheduledService: loop error; retrying in 5 min.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ContainerAllocationDataSyncService>();
        try
        {
            var result = await svc.SyncBoxesToWmsProdAsync(origin: "Scheduled", actor: "system (scheduled)", ct: ct);
            log.LogInformation("BoxesToWmsProd scheduled push: {Msg}", result.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "BoxesToWmsProdScheduledService: SyncBoxesToWmsProdAsync threw.");
        }
    }
}
