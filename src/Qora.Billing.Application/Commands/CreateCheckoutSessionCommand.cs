using MediatR;

namespace Qora.Billing.Application.Commands;

public record CreateCheckoutSessionCommand(Guid TenantId, Guid PlanId) : IRequest<string>;
