using Dispatch.Core.Data;
using Dispatch.Core.Models;

namespace Dispatch.Core.Services;

/// <summary>
/// Files the "your job is done" note in the in-app Inbox so the demo works
/// without SMTP. Replace this class with a SendGrid / Graph / SmtpClient
/// implementation — <see cref="IEmailSender"/> is the seam.
/// </summary>
public sealed class InboxEmailSender : IEmailSender
{
    private readonly AppDbContext _db;
    private readonly ILogger<InboxEmailSender> _log;

    public InboxEmailSender(AppDbContext db, ILogger<InboxEmailSender> log)
    {
        _db = db;
        _log = log;
    }

    public async Task SendJobCompletedAsync(
        string to,
        string title,
        Guid jobId,
        string sessionUrl,
        CancellationToken cancellationToken)
    {
        var subject = $"{title} is ready";
        var preview = $"Your detached job finished. Open the session to read the report.";
        var body = $"""
            <p style="margin:0 0 12px">The night-shift run of <strong>{Html(title)}</strong> has finished.</p>
            <p style="margin:0 0 16px">Results are filed on a capability URL — anyone with the link can open the session.</p>
            <p style="margin:0 0 20px">
              <a href="{Html(sessionUrl)}" style="display:inline-block;padding:10px 16px;background:#d7dbe2;color:#0b0c0e;text-decoration:none;font-weight:600;border-radius:4px">
                Open session
              </a>
            </p>
            <p style="margin:0;font-size:12px;opacity:.7">Session link:<br />{Html(sessionUrl)}</p>
            """;

        _db.InboxMessages.Add(new InboxMessage
        {
            Id = Guid.NewGuid(),
            To = to,
            Subject = subject,
            Preview = preview,
            BodyHtml = body,
            JobId = jobId,
            SentUtc = DateTime.UtcNow,
            IsRead = false
        });

        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation("Filed inbox note for {JobId} to {To}. Session {Url}", jobId, to, sessionUrl);
    }

    private static string Html(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
