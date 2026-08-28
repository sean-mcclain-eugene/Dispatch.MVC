using System.Threading.Channels;

namespace Dispatch.Mvc.Services;

public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public void QueueJob(Guid jobId)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        if (!_queue.Writer.TryWrite(jobId))
        {
            throw new InvalidOperationException("Background queue is closed.");
        }
    }

    public ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken) =>
        _queue.Reader.ReadAsync(cancellationToken);

    Task<Guid> IBackgroundTaskQueue.DequeueAsync(CancellationToken cancellationToken) =>
        DequeueAsync(cancellationToken).AsTask();
}
