using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetTenantByIdQuery(Guid TenantId) : IRequest<TenantResponse?>;
