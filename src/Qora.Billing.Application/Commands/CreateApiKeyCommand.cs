using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record CreateApiKeyCommand(
    Guid TenantId,
    CreateApiKeyRequest Request) : IRequest<ApiKeyResponse>;
