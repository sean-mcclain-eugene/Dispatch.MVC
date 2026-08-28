namespace Dispatch.Mvc.Services;

public interface IJobProcessor
{
    Task RunAsync(Guid jobId, CancellationToken cancellationToken);
}
