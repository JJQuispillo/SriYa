using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetDocumentsByTenantQueryHandler
    : IRequestHandler<GetDocumentsByTenantQuery, PaginatedResponse<DocumentResponse>>
{
    private readonly IDocumentRepository _documentRepository;

    public GetDocumentsByTenantQueryHandler(IDocumentRepository documentRepository)
    {
        _documentRepository = documentRepository;
    }

    public async Task<PaginatedResponse<DocumentResponse>> Handle(
        GetDocumentsByTenantQuery query, CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _documentRepository.GetByTenantIdAsync(
            query.TenantId, query.Page, query.PageSize, cancellationToken);

        var responses = items.Select(MapToResponse).ToList();
        var totalPages = (int)Math.Ceiling((double)totalCount / query.PageSize);

        return new PaginatedResponse<DocumentResponse>(
            responses, query.Page, query.PageSize, totalCount, totalPages);
    }

    private static DocumentResponse MapToResponse(Document document)
    {
        return new DocumentResponse(
            document.Id,
            document.TenantId,
            document.DocumentType,
            document.AccessKey?.Value,
            document.Status,
            document.SriAuthorizationNumber,
            document.SriAuthorizationDate,
            document.ErrorMessage,
            document.CreatedAt,
            document.ProcessedAt);
    }
}
