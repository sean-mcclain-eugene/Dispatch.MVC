namespace Dispatch.Core.Models;

public static class JobStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    /// <summary>
    /// Attached run whose browser went away (tab closed, navigate, crash)
    /// or whose app pool stopped while the user was still watching.
    /// Not a failure of the work itself — we refused to keep burning IIS.
    /// </summary>
    public const string Abandoned = "Abandoned";

    public static bool IsTerminal(string status) =>
        status is Completed or Failed or Abandoned;
}
