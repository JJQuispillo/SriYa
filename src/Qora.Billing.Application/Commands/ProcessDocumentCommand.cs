using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record ProcessDocumentCommand(
    Guid TenantId,
    CreateDocumentRequest Request,
    string? IdempotencyKey = null) : IRequest<DocumentResponse>;
