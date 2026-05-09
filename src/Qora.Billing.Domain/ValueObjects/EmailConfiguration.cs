namespace Qora.Billing.Domain.ValueObjects;

/// <summary>
/// Immutable SMTP configuration value object used for sending emails.
/// </summary>
public record EmailConfiguration(
    string SmtpHost,
    int SmtpPort,
    string SmtpUser,
    string SmtpPassword,
    bool UseSsl,
    string SenderEmail,
    string SenderName);
