using MediatR;

namespace Qora.Billing.Application.Queries;

public record GetDocumentPdfQuery(Guid TenantId, Guid DocumentId) : IRequest<byte[]?>;
