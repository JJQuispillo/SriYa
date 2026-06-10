using MediatR;

namespace Qora.Billing.Application.Commands;

public record RevokeApiKeyCommand(Guid TenantId, Guid ApiKeyId) : IRequest<bool>;
