namespace Qora.Billing.Application.Settings;

/// <summary>
/// SMTP settings for the Qora-managed email provider.
/// Bound to the "Qora:Email" section in appsettings.
/// </summary>
public class QoraEmailSettings
{
    public const string SectionName = "Qora:Email";

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
    public string SenderEmail { get; set; } = "noreply@qora.io";
    public string SenderName { get; set; } = "Qora Billing";
}
