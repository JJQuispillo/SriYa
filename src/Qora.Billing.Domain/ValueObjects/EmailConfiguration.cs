namespace Qora.Billing.Domain.ValueObjects;

/// <summary>
/// Value object inmutable de configuración SMTP usado para el envío de emails.
/// </summary>
public record EmailConfiguration(
    string SmtpHost,
    int SmtpPort,
    string SmtpUser,
    string SmtpPassword,
    bool UseSsl,
    string SenderEmail,
    string SenderName);
