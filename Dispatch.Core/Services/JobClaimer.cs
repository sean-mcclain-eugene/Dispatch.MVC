using Dispatch.Core.Data;
using Dispatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Core.Services;

/// <summary>
/// Database-backed queue. The web app only inserts <c>Pending</c> rows.
/// The Windows Worker Service claims them here — nothing runs inside IIS.
/// </summary>
public static class JobClaimer
{
    public static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(45);

    /// <summary>Captured as a parameter in ExecuteUpdate (do not inline <c>(DateTime?)null</c>).</summary>
    public static readonly DateTime? Unlocked = null;

    public static async Task<Guid?> TryClaimAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var candidate = await db.Jobs.AsNoTracking()
            .Where(j =>
                j.Status == JobStatuses.Pending
                || (j.Status == JobStatuses.Running
                    && (j.LockUntilUtc == null || j.LockUntilUtc < now)))
            .OrderBy(j => j.CreatedUtc)
            .Select(j => j.JobId)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate == Guid.Empty)
        {
            return null;
        }

        var until = now.Add(LockDuration);
        var rows = await db.Jobs
            .Where(j => j.JobId == candidate
                        && (j.Status == JobStatuses.Pending
                            || (j.Status == JobStatuses.Running
                                && (j.LockUntilUtc == null || j.LockUntilUtc < now))))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, JobStatuses.Running)
                    .SetProperty(x => x.LockUntilUtc, until),
                cancellationToken);

        return rows == 1 ? candidate : null;
    }

    public static Task RenewLockAsync(AppDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow.Add(LockDuration);
        return db.Jobs.Where(j => j.JobId == jobId && j.Status == JobStatuses.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockUntilUtc, until), cancellationToken);
    }
}
