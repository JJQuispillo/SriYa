using MediatR;

namespace Qora.Billing.Application.Commands.Email;

public record TestEmailCommand(Guid TenantId) : IRequest<bool>;
