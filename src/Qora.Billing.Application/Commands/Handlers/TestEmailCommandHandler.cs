using MediatR;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Qora.Billing.Application.Commands.Handlers;

public class TestEmailCommandHandler(
    ITenantRepository tenantRepository,
    IEmailService emailService,
    IOptions<QoraEmailSettings> qoraSettings) : IRequestHandler<TestEmailCommand, bool>
{
    public async Task<bool> Handle(TestEmailCommand command, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");

        EmailConfiguration config;

        if (tenant.EmailProvider == EmailProvider.Custom)
        {
            config = new EmailConfiguration(
                tenant.SmtpHost ?? string.Empty,
                tenant.SmtpPort ?? 587,
                tenant.SmtpUser ?? string.Empty,
                tenant.SmtpPassword ?? string.Empty,
                tenant.UseSsl,
                tenant.SenderEmail ?? string.Empty,
                tenant.SenderName ?? "Facturación");
        }
        else
        {
            var s = qoraSettings.Value;
            config = new EmailConfiguration(
                s.SmtpHost,
                s.SmtpPort,
                s.SmtpUser,
                s.SmtpPassword,
                s.UseSsl,
                s.SenderEmail,
                s.SenderName);
        }

        return await emailService.TestConnectionAsync(config, cancellationToken);
    }
}
