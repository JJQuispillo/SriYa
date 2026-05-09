using MediatR;
using Qora.Billing.Application.DTOs.Email;
using Qora.Billing.Application.Queries.Email;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetEmailSettingsQueryHandler(ITenantRepository tenantRepository)
    : IRequestHandler<GetEmailSettingsQuery, EmailSettingsDto?>
{
    public async Task<EmailSettingsDto?> Handle(GetEmailSettingsQuery query, CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.GetByIdAsync(query.TenantId, cancellationToken);
        if (tenant is null)
            return null;

        return new EmailSettingsDto(
            tenant.EmailEnabled,
            tenant.EmailProvider,
            tenant.SmtpHost,
            tenant.SmtpPort,
            tenant.SmtpUser,
            tenant.UseSsl,
            tenant.SenderEmail,
            tenant.SenderName);
    }
}
