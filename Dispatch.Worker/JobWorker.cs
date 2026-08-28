using Dispatch.Core.Data;
using Dispatch.Core.Services;

namespace Dispatch.Worker;

/// <summary>
/// The process that actually runs jobs. Install as a Windows Service so
/// it outlives IIS app-pool recycle, idle timeout, and the browser.
/// Claims rows from the shared database — no in-memory Channel.
/// </summary>
public sealed class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobWorker> _log;

    public JobWorker(IServiceScopeFactory scopes, ILogger<JobWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Dispatch worker service listening for jobs.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? jobId = null;
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                jobId = await JobClaimer.TryClaimAsync(db, stoppingToken);

                if (jobId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                _log.LogInformation("Claimed job {JobId}.", jobId);
                var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();
                await processor.RunAsync(jobId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop failed on job {JobId}.", jobId);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _log.LogInformation("Dispatch worker service stopping.");
    }
}
