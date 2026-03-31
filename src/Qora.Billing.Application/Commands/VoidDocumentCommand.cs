using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Commands;

public record VoidDocumentCommand(Guid TenantId, Guid DocumentId, string Reason) : IRequest<DocumentResponse>;
