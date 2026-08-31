namespace Dispatch.Worker;

/// <summary>
/// Caps how many jobs this process runs at once. SQL Server is fine with
/// several sessions; the risk is unbounded tasks, not the database.
/// </summary>
public sealed class WorkerOptions
{
    public const int MinConcurrent = 1;
    public const int MaxConcurrent = 5;

    /// <summary>
    /// In-flight jobs in this process. Clamped to 1–5. Extra Pending rows wait.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>
    /// Wall-clock ceiling per job. Cancels and marks Failed so a stuck loop
    /// cannot run forever after a crash/restart cycle.
    /// </summary>
    public TimeSpan MaxJobDuration { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan PollIdle { get; set; } = TimeSpan.FromSeconds(1);

    public int ClampConcurrency() =>
        Math.Clamp(MaxConcurrentJobs, MinConcurrent, MaxConcurrent);
}
