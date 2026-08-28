using System.ComponentModel.DataAnnotations;

namespace Dispatch.Core.Models;

public class LongRunningJob
{
    [Key]
    public Guid JobId { get; set; }

    [Required, MaxLength(32)]
    public string Kind { get; set; } = "sales";

    [Required, MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Where the night-shift note is filed. Stored on the job so the worker
    /// can send after the HTTP request is long gone. Not a login.
    /// </summary>
    [Required, EmailAddress, MaxLength(200)]
    public string NotifyEmail { get; set; } = string.Empty;

    [Required, MaxLength(24)]
    public string Status { get; set; } = JobStatuses.Pending;

    /// <summary>
    /// False = leased to the Status tab; close the browser and we abandon.
    /// True = user opted into night shift; the worker keeps going after
    /// the client is gone (still dies with the IIS app pool unless recovered).
    /// </summary>
    public bool IsDetached { get; set; }

    public int CurrentStep { get; set; }

    public int StepCount { get; set; }

    [MaxLength(200)]
    public string StepLabel { get; set; } = "Queued";

    /// <summary>
    /// JSON array of completed step labels. Worker patches this each tick.
    /// </summary>
    public string CompletedStepsJson { get; set; } = "[]";

    /// <summary>
    /// Seeded report payload written on completion.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// Captured at queue time. BackgroundService has no HttpContext, so the
    /// session link in the email has to be built from this.
    /// </summary>
    [MaxLength(200)]
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Last time the Status page polled. Ignored once <see cref="IsDetached"/>.
    /// </summary>
    public DateTime? LastHeartbeatUtc { get; set; }

    /// <summary>
    /// Worker claim. A dead Windows Service lets another instance take the
    /// row once this is in the past. Null once the job is terminal.
    /// </summary>
    public DateTime? LockUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}
