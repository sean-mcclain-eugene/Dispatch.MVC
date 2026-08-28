namespace Dispatch.Mvc.Services;

/// <summary>
/// In-process work queue. Production apps typically swap this for Hangfire,
/// Azure Service Bus, or a database-backed outbox — the controller still only
/// enqueues a job id and returns.
/// </summary>
public interface IBackgroundTaskQueue
{
    void QueueJob(Guid jobId);

    Task<Guid> DequeueAsync(CancellationToken cancellationToken);
}
