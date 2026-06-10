using MediatR;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.Commands.Email;

public record ConfigureEmailSettingsCommand(
    Guid TenantId,
    bool EmailEnabled,
    EmailProvider EmailProvider,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUser,
    string? SmtpPassword,
    bool UseSsl,
    string? SenderEmail,
    string? SenderName) : IRequest;
