using System.ComponentModel.DataAnnotations;

namespace Dispatch.Core.Models;

/// <summary>
/// In-app stand-in for a real mailbox. Swap <see cref="Services.IEmailSender"/>
/// for SMTP / SendGrid / Graph — keep this table if you still want an operator
/// console of what was sent.
/// </summary>
public class InboxMessage
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(200)]
    public string To { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string BodyHtml { get; set; } = string.Empty;

    [MaxLength(400)]
    public string Preview { get; set; } = string.Empty;

    public Guid JobId { get; set; }

    public DateTime SentUtc { get; set; } = DateTime.UtcNow;

    public bool IsRead { get; set; }
}
