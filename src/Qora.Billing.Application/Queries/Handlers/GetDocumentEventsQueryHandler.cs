using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetDocumentEventsQueryHandler : IRequestHandler<GetDocumentEventsQuery, List<DocumentEventResponse>>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentEventRepository _documentEventRepository;

    public GetDocumentEventsQueryHandler(
        IDocumentRepository documentRepository,
        IDocumentEventRepository documentEventRepository)
    {
        _documentRepository = documentRepository;
        _documentEventRepository = documentEventRepository;
    }

    public async Task<List<DocumentEventResponse>> Handle(
        GetDocumentEventsQuery query, CancellationToken cancellationToken)
    {
        // Verify document belongs to tenant
        var document = await _documentRepository.GetByIdAsync(query.DocumentId, cancellationToken);
        if (document is null || document.TenantId != query.TenantId)
            throw new BillingDomainException($"Documento {query.DocumentId} no encontrado para el tenant {query.TenantId}.");

        var events = await _documentEventRepository.GetByDocumentIdAsync(query.DocumentId, cancellationToken);

        return events.Select(e => new DocumentEventResponse(
            e.EventType, e.Description, e.OccurredAt)).ToList();
    }
}
