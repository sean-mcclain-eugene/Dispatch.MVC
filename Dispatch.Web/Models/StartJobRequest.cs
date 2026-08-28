using System.ComponentModel.DataAnnotations;

namespace Dispatch.Web.Models;

public class StartJobRequest
{
    [Required]
    [RegularExpression("^(sales|export|index|invoices|inventory)$")]
    public string Kind { get; set; } = "sales";

    [Required, EmailAddress, MaxLength(200)]
    [Display(Name = "Notify email")]
    public string NotifyEmail { get; set; } = string.Empty;
}
