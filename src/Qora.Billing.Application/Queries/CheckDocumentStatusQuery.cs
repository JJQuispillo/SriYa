using MediatR;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Queries;

public record CheckDocumentStatusQuery(Guid TenantId, Guid DocumentId) : IRequest<DocumentResponse?>;
