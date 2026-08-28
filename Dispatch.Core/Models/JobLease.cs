namespace Dispatch.Core.Models;

/// <summary>
/// Attached jobs are leased to the live Status tab. Detached jobs are not.
/// On IIS this is the difference between "user closed the browser" (stop)
/// and "user asked for night shift" (keep going in w3wp until recycle).
/// </summary>
public static class JobLease
{
    /// <summary>
    /// Progress polls every ~700ms. 20s covers a backgrounded tab without
    /// treating a brief blip as a close. sendBeacon on pagehide is the
    /// fast path; this is the crash / killed-tab fallback.
    /// </summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Worker sleeps in slices so a close is noticed without waiting out
    /// the whole step delay (~3s).
    /// </summary>
    public static readonly TimeSpan WatchSlice = TimeSpan.FromMilliseconds(400);

    public static bool IsExpired(DateTime? lastHeartbeatUtc, DateTime utcNow) =>
        lastHeartbeatUtc is null
        || utcNow - lastHeartbeatUtc.Value > HeartbeatTimeout;
}
