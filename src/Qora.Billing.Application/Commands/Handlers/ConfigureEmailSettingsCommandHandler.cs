using MediatR;
using Qora.Billing.Application.Commands.Email;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class ConfigureEmailSettingsCommandHandler(
    ITenantRepository tenantRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ConfigureEmailSettingsCommand>
{
    public async Task Handle(ConfigureEmailSettingsCommand command, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");

        tenant.ConfigureEmail(
            command.EmailEnabled,
            command.EmailProvider,
            command.SmtpHost,
            command.SmtpPort,
            command.SmtpUser,
            command.SmtpPassword,
            command.UseSsl,
            command.SenderEmail,
            command.SenderName);

        await tenantRepository.UpdateAsync(tenant, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
