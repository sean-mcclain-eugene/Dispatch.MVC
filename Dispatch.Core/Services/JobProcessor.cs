using Dispatch.Core.Data;
using Dispatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Core.Services;

/// <summary>
/// Does the actual work. Runs inside the Windows Worker Service — never
/// inside IIS. Uses ExecuteUpdate so a concurrent Detach / Abandon cannot
/// be overwritten by a stale tracked entity.
///
/// Attached jobs are leased to the browser: if the tab closes we stop.
/// Detached jobs ignore the lease and run until they finish or the service
/// stops (lock expires, next instance resumes).
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

        if (JobStatuses.IsTerminal(job.Status))
        {
            return;
        }

        var catalog = JobCatalog.Get(job.Kind);
        var completed = ParseSteps(job.CompletedStepsJson);
        var startAt = Math.Max(0, completed.Count);

        try
        {
            var firstLabel = catalog.Steps[Math.Min(startAt, catalog.Steps.Count - 1)];
            var started = job.StartedUtc ?? DateTime.UtcNow;
            var lockUntil = DateTime.UtcNow.Add(JobClaimer.LockDuration);
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Running)
                .SetProperty(x => x.StartedUtc, started)
                .SetProperty(x => x.LockUntilUtc, lockUntil)
                .SetProperty(x => x.StepLabel, firstLabel)
                .SetProperty(x => x.CurrentStep, Math.Min(startAt + 1, catalog.Steps.Count)),
                cancellationToken);

            for (var i = startAt; i < catalog.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await StopIfClientGone(jobId, cancellationToken))
                {
                    return;
                }

                var label = catalog.Steps[i];
                var snapshot = Serialize(completed);
                var stepNumber = i + 1;
                await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CurrentStep, stepNumber)
                    .SetProperty(x => x.StepLabel, label)
                    .SetProperty(x => x.CompletedStepsJson, snapshot),
                    cancellationToken);

                if (!await DelayWatchingLease(jobId, catalog.StepDelay, cancellationToken))
                {
                    return;
                }

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
                .SetProperty(x => x.ResultJson, resultJson)
                .SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked),
                cancellationToken);

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
            // Host stop vs MaxJobDuration is decided by JobWorker so a timeout
            // becomes Failed (zombie) and a service restart stays resumable.
            _log.LogInformation("Job {JobId} cancelled.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {JobId} failed.", jobId);
            var message = ex.Message;
            await _db.Jobs.Where(j => j.JobId == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Failed)
                .SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked)
                .SetProperty(x => x.Error, message));
        }
    }

    /// <summary>
    /// Returns false if the job should stop (abandoned / gone).
    /// </summary>
    private async Task<bool> DelayWatchingLease(
        Guid jobId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await StopIfClientGone(jobId, cancellationToken))
            {
                return false;
            }

            var slice = remaining < JobLease.WatchSlice ? remaining : JobLease.WatchSlice;
            await Task.Delay(slice, cancellationToken);
            remaining -= slice;
            await JobClaimer.RenewLockAsync(_db, jobId, cancellationToken);
        }

        return !await StopIfClientGone(jobId, cancellationToken);
    }

    private async Task<bool> StopIfClientGone(Guid jobId, CancellationToken cancellationToken)
    {
        var state = await _db.Jobs.AsNoTracking()
            .Where(j => j.JobId == jobId)
            .Select(j => new { j.Status, j.IsDetached, j.LastHeartbeatUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (state is null || JobStatuses.IsTerminal(state.Status))
        {
            return true;
        }

        if (state.IsDetached)
        {
            return false;
        }

        if (!JobLease.IsExpired(state.LastHeartbeatUtc, DateTime.UtcNow))
        {
            return false;
        }

        await Abandon(jobId, "Browser closed or stopped polling; attached run released the worker.");
        _log.LogInformation("Abandoned attached job {JobId} after lease expired.", jobId);
        return true;
    }

    private Task<int> Abandon(Guid jobId, string reason) =>
        _db.Jobs
            .Where(j => j.JobId == jobId
                        && !j.IsDetached
                        && j.Status != JobStatuses.Completed
                        && j.Status != JobStatuses.Failed
                        && j.Status != JobStatuses.Abandoned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Abandoned)
                .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                .SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked)
                .SetProperty(x => x.Error, reason));

    private static string Serialize(IReadOnlyList<string> steps) =>
        System.Text.Json.JsonSerializer.Serialize(steps);

    private static List<string> ParseSteps(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
