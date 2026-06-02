using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs.Email;

/// <summary>
/// DTO de configuración de correo devuelto al consumidor de la API.
/// SmtpPassword se excluye intencionalmente por seguridad.
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
