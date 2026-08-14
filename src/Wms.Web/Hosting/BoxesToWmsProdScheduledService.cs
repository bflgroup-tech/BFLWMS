using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every 30 minutes to push unpublished WmsUPCBoxHead + WmsUPCBoxDet
/// rows to on-prem usa.dbo.upcboxhead + usa.dbo.upcboxdet.
///
/// TWO gates, both must be on — this job writes to on-prem PRODUCTION tables:
///   1. Config flag `Scheduler:BoxesToWmsProd:Enabled` (default false) — the
///      infrastructure-level kill switch, set in App Service configuration.
///      The service is registered unconditionally so the flag can be flipped at
///      runtime without a redeploy.
///   2. dbo.WmsRptCountryConfig row (JobName='BoxesToWmsProd', Country='') —
///      the day-to-day toggle on the Nightly Batches page.
///
/// The config flag deliberately wins: with it off, the page shows the toggle as
/// disabled rather than pretending the UI controls anything. Each fire writes a
/// dbo.WmsRptJobRun row so the pushes appear in Recent runs.
/// </summary>
public class BoxesToWmsProdScheduledService(
    IServiceProvider sp,
    IConfiguration cfg,
    ILogger<BoxesToWmsProdScheduledService> log)
    : BackgroundService
{
    public const string JobName = "BoxesToWmsProd";
    public const string EnabledConfigKey = "Scheduler:BoxesToWmsProd:Enabled";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("BoxesToWmsProdScheduledService started. Fires every 30 min when {Key}=true and the job toggle is active.", EnabledConfigKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (cfg.GetValue(EnabledConfigKey, false))
                    await RunAsync(stoppingToken);

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
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();

        if (!await jobs.IsActiveAsync(JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogDebug("BoxesToWmsProd: job toggle is inactive — skipping this tick.");
            return;
        }

        var svc   = scope.ServiceProvider.GetRequiredService<ContainerAllocationDataSyncService>();
        var runId = await jobs.StartRunAsync(JobName, "Interval", null, "Timer", ct);
        try
        {
            var result = await svc.SyncBoxesToWmsProdAsync(origin: "Scheduled", actor: "system (scheduled)", ct: ct);
            await jobs.FinishRunAsync(runId, "Success", null, result.Message, ct);
            log.LogInformation("BoxesToWmsProd scheduled push: {Msg}", result.Message);
        }
        catch (Exception ex)
        {
            await jobs.FinishRunAsync(runId, "Failed", null, ex.Message, CancellationToken.None);
            log.LogError(ex, "BoxesToWmsProdScheduledService: SyncBoxesToWmsProdAsync threw.");
        }
    }
}
