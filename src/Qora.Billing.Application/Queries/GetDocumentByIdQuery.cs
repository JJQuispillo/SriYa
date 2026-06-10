using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetDocumentByIdQuery(Guid TenantId, Guid DocumentId) : IRequest<DocumentResponse?>;
