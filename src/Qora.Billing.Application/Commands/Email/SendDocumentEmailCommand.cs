using MediatR;

namespace Qora.Billing.Application.Commands.Email;

public record SendDocumentEmailCommand(Guid DocumentId, Guid TenantId) : IRequest<bool>;
