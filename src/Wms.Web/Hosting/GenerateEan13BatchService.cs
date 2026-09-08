using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires every 15 minutes to backfill DATAREPORTING.dbo.UPC_SUBCLASS.EAN13 via
/// GenerateEan13Service. Gated by the usual dbo.WmsRptCountryConfig toggle
/// (JobName='GenerateEAN13', Country='') on the Nightly Batches admin page —
/// same convention as BoxesToWmsProdScheduledService, which also writes to an
/// on-prem production table on an interval.
/// </summary>
public class GenerateEan13BatchService(IServiceProvider sp, ILogger<GenerateEan13BatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("GenerateEan13BatchService started. Fires every 15 min when the job toggle is active.");

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
                log.LogError(ex, "GenerateEan13BatchService: loop error; retrying in 5 min.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScheduledJobService>();

        if (!await jobs.IsActiveAsync(GenerateEan13Service.JobName, ScheduledJobService.SingleRowKey, ct))
        {
            log.LogDebug("GenerateEAN13: job toggle is inactive — skipping this tick.");
            return;
        }

        var svc = scope.ServiceProvider.GetRequiredService<GenerateEan13Service>();
        var (rows, err) = await svc.RunOnceAsync("Interval", "Timer", ct);
        if (err is null)
            log.LogInformation("GenerateEan13BatchService: {Rows} rows updated.", rows);
        else
            log.LogError("GenerateEan13BatchService: FAILED — {Error}", err);
    }
}
