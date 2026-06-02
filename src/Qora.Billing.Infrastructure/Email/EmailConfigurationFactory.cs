using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Métodos de fábrica para construir <see cref="EmailConfiguration"/> a partir de appsettings o datos del tenant.
/// Se mantiene en Infrastructure para evitar una dependencia Domain → Application.
/// </summary>
public static class EmailConfigurationFactory
{
    public static EmailConfiguration ForQora(QoraEmailSettings settings) =>
        new(
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpUser,
            settings.SmtpPassword,
            settings.UseSsl,
            settings.SenderEmail,
            settings.SenderName);

    public static EmailConfiguration ForCustom(Tenant tenant) =>
        new(
            tenant.SmtpHost ?? string.Empty,
            tenant.SmtpPort ?? 587,
            tenant.SmtpUser ?? string.Empty,
            tenant.SmtpPassword ?? string.Empty,
            tenant.UseSsl,
            tenant.SenderEmail ?? string.Empty,
            tenant.SenderName ?? string.Empty);
}
