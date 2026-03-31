using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record GetDocumentEventsQuery(Guid TenantId, Guid DocumentId) : IRequest<List<DocumentEventResponse>>;
