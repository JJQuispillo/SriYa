using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs.Email;

/// <summary>
/// Email settings DTO returned to the API consumer.
/// SmtpPassword is intentionally excluded for security.
/// </summary>
public record EmailSettingsDto(
    bool EmailEnabled,
    EmailProvider EmailProvider,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    bool UseSsl,
    string? SenderEmail,
    string? SenderName);
