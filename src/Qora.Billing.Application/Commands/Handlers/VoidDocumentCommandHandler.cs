using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class VoidDocumentCommandHandler : IRequestHandler<VoidDocumentCommand, DocumentResponse>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentEventRepository _documentEventRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VoidDocumentCommandHandler(
        IDocumentRepository documentRepository,
        IDocumentEventRepository documentEventRepository,
        IUnitOfWork unitOfWork)
    {
        _documentRepository = documentRepository;
        _documentEventRepository = documentEventRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<DocumentResponse> Handle(VoidDocumentCommand command, CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetByIdAsync(command.DocumentId, cancellationToken)
            ?? throw new BillingDomainException($"Documento {command.DocumentId} no encontrado.");

        if (document.TenantId != command.TenantId)
            throw new BillingDomainException($"El documento {command.DocumentId} no pertenece al tenant {command.TenantId}.");

        document.Void(command.Reason);

        await _documentRepository.UpdateAsync(document, cancellationToken);
        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.Voided, $"Documento anulado: {command.Reason}"),
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
