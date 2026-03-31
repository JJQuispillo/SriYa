using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record ProcessDocumentCommand(
    Guid TenantId,
    CreateDocumentRequest Request) : IRequest<DocumentResponse>;
