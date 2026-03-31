using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record CreateTenantCommand(CreateTenantRequest Request) : IRequest<TenantResponse>;
