using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

/// <summary>
/// Factory methods for building <see cref="EmailConfiguration"/> from appsettings or tenant data.
/// Kept in Infrastructure to avoid a Domain → Application dependency.
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
