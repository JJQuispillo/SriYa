using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record UpdateTenantCommand(Guid TenantId, UpdateTenantRequest Request) : IRequest<TenantResponse>;
