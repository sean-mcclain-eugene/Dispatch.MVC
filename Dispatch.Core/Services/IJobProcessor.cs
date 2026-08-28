namespace Dispatch.Core.Services;

public interface IJobProcessor
{
    Task RunAsync(Guid jobId, CancellationToken cancellationToken);
}
