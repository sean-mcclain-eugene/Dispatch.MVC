using Dispatch.Mvc.Data;
using Dispatch.Mvc.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Mvc.Services;

/// <summary>
/// Does the actual work. Uses ExecuteUpdate so a concurrent Detach POST
/// cannot be overwritten by a stale tracked entity (the original worker
/// held one LongRunningJob across awaits and clobbered IsDetached).
/// </summary>
public sealed class JobProcessor : IJobProcessor
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<JobProcessor> _log;

    public JobProcessor(AppDbContext db, IEmailSender email, ILogger<JobProcessor> log)
    {
        _db = db;
        _email = email;
        _log = log;
    }

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        if (job is null)
        {
            _log.LogWarning("Job {JobId} was queued but not found.", jobId);
            return;
        }

        var catalog = JobCatalog.Get(job.Kind);
        var completed = new List<string>();

        try
        {
            var firstLabel = catalog.Steps[0];
            var started = DateTime.UtcNow;
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Running)
                .SetProperty(x => x.StartedUtc, started)
                .SetProperty(x => x.StepLabel, firstLabel)
                .SetProperty(x => x.CurrentStep, 1), cancellationToken);

            for (var i = 0; i < catalog.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var label = catalog.Steps[i];
                var snapshot = Serialize(completed);
                var stepNumber = i + 1;
                await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CurrentStep, stepNumber)
                    .SetProperty(x => x.StepLabel, label)
                    .SetProperty(x => x.CompletedStepsJson, snapshot),
                    cancellationToken);

                await Task.Delay(catalog.StepDelay, cancellationToken);
                completed.Add(label);
            }

            var resultJson = JobResultFactory.Create(job.Kind, jobId);
            var doneSteps = Serialize(completed);
            var completedAt = DateTime.UtcNow;
            var lastStep = catalog.Steps.Count;
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Completed)
                .SetProperty(x => x.CompletedUtc, completedAt)
                .SetProperty(x => x.CurrentStep, lastStep)
                .SetProperty(x => x.StepLabel, "Signed")
                .SetProperty(x => x.CompletedStepsJson, doneSteps)
                .SetProperty(x => x.ResultJson, resultJson),
                cancellationToken);

            // Re-read detach + email after the last write. The user may have
            // converted the job to night shift while we were in Task.Delay.
            var fresh = await _db.Jobs.AsNoTracking()
                .Where(j => j.JobId == jobId)
                .Select(j => new { j.IsDetached, j.NotifyEmail, j.PublicBaseUrl, j.Title })
                .FirstAsync(cancellationToken);

            if (fresh.IsDetached && !string.IsNullOrWhiteSpace(fresh.NotifyEmail))
            {
                var sessionUrl = $"{fresh.PublicBaseUrl.TrimEnd('/')}/Job/Session/{jobId:D}";
                await _email.SendJobCompletedAsync(
                    fresh.NotifyEmail,
                    fresh.Title,
                    jobId,
                    sessionUrl,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Failed)
                .SetProperty(x => x.Error, "Cancelled when the host stopped."));
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {JobId} failed.", jobId);
            var message = ex.Message;
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Failed)
                .SetProperty(x => x.Error, message));
        }
    }

    private static string Serialize(IReadOnlyList<string> steps) =>
        System.Text.Json.JsonSerializer.Serialize(steps);
}
