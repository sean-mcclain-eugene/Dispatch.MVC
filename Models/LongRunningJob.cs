using System.ComponentModel.DataAnnotations;

namespace Dispatch.Mvc.Models;

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

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}
