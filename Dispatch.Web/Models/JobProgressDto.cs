namespace Dispatch.Web.Models;

public sealed record JobProgressDto(
    Guid JobId,
    string Kind,
    string Title,
    string Status,
    bool IsDetached,
    int CurrentStep,
    int StepCount,
    string StepLabel,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> CompletedSteps,
    double Progress,
    string NotifyEmail,
    DateTime CreatedUtc,
    DateTime? CompletedUtc);
