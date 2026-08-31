using System.Collections.Concurrent;
using Dispatch.Core.Data;
using Dispatch.Core.Models;
using Dispatch.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dispatch.Worker;

/// <summary>
/// Claims jobs from SQL (or sqlite) and runs up to N of them in parallel.
/// N is <see cref="WorkerOptions.MaxConcurrentJobs"/> (1–5). The claim loop
/// never starts a sixth task; extra rows stay Pending.
///
/// Zombie guards: concurrency cap, per-job timeout, in-flight registry
/// (this process will not reclaim its own running ids), lock expiry for a
/// dead process, and browser lease for attached jobs.
/// </summary>
public sealed class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkerOptions _options;
    private readonly ILogger<JobWorker> _log;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public JobWorker(
        IServiceScopeFactory scopes,
        IOptions<WorkerOptions> options,
        ILogger<JobWorker> log)
    {
        _scopes = scopes;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cap = _options.ClampConcurrency();
        using var gate = new SemaphoreSlim(cap, cap);
        _log.LogInformation(
            "Dispatch worker listening. MaxConcurrentJobs={Cap}, MaxJobDuration={Duration}.",
            cap,
            _options.MaxJobDuration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await gate.WaitAsync(_options.PollIdle, stoppingToken))
                {
                    continue;
                }

                Guid? jobId;
                try
                {
                    jobId = await TryClaimAsync(stoppingToken);
                }
                catch
                {
                    gate.Release();
                    throw;
                }

                if (jobId is null)
                {
                    gate.Release();
                    await Task.Delay(_options.PollIdle, stoppingToken);
                    continue;
                }

                var timeoutCts = new CancellationTokenSource(_options.MaxJobDuration);
                _inFlight[jobId.Value] = RunClaimedAsync(jobId.Value, gate, timeoutCts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker claim loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        var leftover = _inFlight.Values.ToArray();
        if (leftover.Length > 0)
        {
            _log.LogInformation("Waiting for {Count} in-flight job(s) to release.", leftover.Length);
            try
            {
                await Task.WhenAll(leftover);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "In-flight jobs finished with errors during shutdown.");
            }
        }

        _log.LogInformation("Dispatch worker service stopping.");
    }

    private async Task<Guid?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var skip = _inFlight.Keys.ToArray();
        return await JobClaimer.TryClaimAsync(db, skip, cancellationToken);
    }

    private async Task RunClaimedAsync(
        Guid jobId,
        SemaphoreSlim gate,
        CancellationTokenSource timeoutCts,
        CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = timeoutCts;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

            _log.LogInformation(
                "Running job {JobId} ({InFlight}/{Cap}).",
                jobId,
                _inFlight.Count,
                _options.ClampConcurrency());

            await using var scope = _scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();
            await processor.RunAsync(jobId, linked.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await UnlockForResumeAsync(jobId);
            _log.LogInformation("Job {JobId} released lock because the worker is stopping.", jobId);
        }
        catch (OperationCanceledException)
        {
            await FailZombieAsync(jobId, "Cancelled after MaxJobDuration (zombie guard).");
            _log.LogWarning("Job {JobId} cancelled as a zombie after {Duration}.", jobId, _options.MaxJobDuration);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Worker failed on job {JobId}.", jobId);
        }
        finally
        {
            _inFlight.TryRemove(jobId, out _);
            gate.Release();
        }
    }

    private async Task UnlockForResumeAsync(Guid jobId)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Jobs.Where(j => j.JobId == jobId && j.Status == JobStatuses.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not release lock for {JobId} on shutdown.", jobId);
        }
    }

    private async Task FailZombieAsync(Guid jobId, string message)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Jobs
                .Where(j => j.JobId == jobId
                            && j.Status != JobStatuses.Completed
                            && j.Status != JobStatuses.Failed
                            && j.Status != JobStatuses.Abandoned)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, JobStatuses.Failed)
                    .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                    .SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked)
                    .SetProperty(x => x.Error, message));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not mark zombie job {JobId} failed.", jobId);
        }
    }
}
