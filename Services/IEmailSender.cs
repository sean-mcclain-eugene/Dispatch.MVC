namespace Dispatch.Mvc.Services;

public interface IEmailSender
{
    Task SendJobCompletedAsync(
        string to,
        string title,
        Guid jobId,
        string sessionUrl,
        CancellationToken cancellationToken);
}
