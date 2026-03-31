using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Qora.Billing.Application.Queries.Handlers;

/// <summary>
/// Re-checks SRI authorization status for a document that was accepted but not yet authorized.
/// </summary>
public class CheckDocumentStatusQueryHandler : IRequestHandler<CheckDocumentStatusQuery, DocumentResponse?>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentEventRepository _documentEventRepository;
    private readonly ISriClient _sriClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CheckDocumentStatusQueryHandler> _logger;

    public CheckDocumentStatusQueryHandler(
        IDocumentRepository documentRepository,
        IDocumentEventRepository documentEventRepository,
        ISriClient sriClient,
        IUnitOfWork unitOfWork,
        ILogger<CheckDocumentStatusQueryHandler> logger)
    {
        _documentRepository = documentRepository;
        _documentEventRepository = documentEventRepository;
        _sriClient = sriClient;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<DocumentResponse?> Handle(CheckDocumentStatusQuery query, CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetByIdAsync(query.DocumentId, cancellationToken);
        if (document is null || document.TenantId != query.TenantId)
            return null;

        // Only re-check documents that are pending (SentToSri or PendingRetry)
        if (document.Status is DocumentStatus.SentToSri or DocumentStatus.PendingRetry)
        {
            if (document.AccessKey is not null)
            {
                var result = await _sriClient.CheckAuthorizationAsync(
                    document.AccessKey.Value, cancellationToken);

                if (result.IsAuthorized && result.AuthorizationNumber is not null
                    && result.AuthorizationDate.HasValue)
                {
                    document.Authorize(result.AuthorizationNumber, result.AuthorizationDate.Value);
                    await _documentRepository.UpdateAsync(document, cancellationToken);
                    await _documentEventRepository.CreateAsync(
                        DocumentEvent.Create(document.Id, query.TenantId, EventType.Authorized,
                            $"Authorized via status check. Auth#: {result.AuthorizationNumber}."),
                        cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation(
                        "Document {DocumentId} authorized via status check", document.Id);
                }
            }
        }

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
