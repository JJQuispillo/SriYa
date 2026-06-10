using MediatR;
using Qora.Billing.Application.DTOs.Email;

namespace Qora.Billing.Application.Queries.Email;

public record GetEmailSettingsQuery(Guid TenantId) : IRequest<EmailSettingsDto?>;
