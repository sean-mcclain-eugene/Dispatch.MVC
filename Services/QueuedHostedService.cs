namespace Dispatch.Mvc.Services;

/// <summary>
/// The process-owned worker. Kestrel can finish the HTTP request (and the
/// user can close the tab) while this keeps running. Do not inject this
/// into a controller — hosted services are singletons; queue a job id instead.
/// </summary>
public sealed class QueuedHostedService : BackgroundService
{
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QueuedHostedService> _log;

    public QueuedHostedService(
        IBackgroundTaskQueue queue,
        IServiceScopeFactory scopes,
        ILogger<QueuedHostedService> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Dispatch worker listening for jobs.");

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            try
            {
                jobId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Fresh scope per job so the processor gets its own DbContext.
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();
                await processor.RunAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Background job {JobId} failed at the host boundary.", jobId);
            }
        }

        _log.LogInformation("Dispatch worker stopping.");
    }
}
