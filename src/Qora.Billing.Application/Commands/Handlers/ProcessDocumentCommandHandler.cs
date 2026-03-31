using MediatR;
using Microsoft.Extensions.Logging;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Extensions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

/// <summary>
/// Core pipeline handler: validates → creates Document → generates XML → signs → sends to SRI → generates RIDE → saves.
/// </summary>
public class ProcessDocumentCommandHandler : IRequestHandler<ProcessDocumentCommand, DocumentResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IElectronicSignatureRepository _signatureRepository;
    private readonly IDocumentEventRepository _documentEventRepository;
    private readonly IUsageRecordRepository _usageRecordRepository;
    private readonly IEnumerable<IDocumentTypeStrategy> _strategies;
    private readonly IDocumentSigner _documentSigner;
    private readonly ISriClient _sriClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ProcessDocumentCommandHandler> _logger;

    public ProcessDocumentCommandHandler(
        ITenantRepository tenantRepository,
        IDocumentRepository documentRepository,
        IElectronicSignatureRepository signatureRepository,
        IDocumentEventRepository documentEventRepository,
        IUsageRecordRepository usageRecordRepository,
        IEnumerable<IDocumentTypeStrategy> strategies,
        IDocumentSigner documentSigner,
        ISriClient sriClient,
        IUnitOfWork unitOfWork,
        ILogger<ProcessDocumentCommandHandler> logger)
    {
        _tenantRepository = tenantRepository;
        _documentRepository = documentRepository;
        _signatureRepository = signatureRepository;
        _documentEventRepository = documentEventRepository;
        _usageRecordRepository = usageRecordRepository;
        _strategies = strategies;
        _documentSigner = documentSigner;
        _sriClient = sriClient;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<DocumentResponse> Handle(ProcessDocumentCommand command, CancellationToken cancellationToken)
    {
        // 1. Validate tenant exists and is active
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} not found.");
        tenant.EnsureActive();

        // 2. Get active certificate
        var signature = await _signatureRepository.GetActiveByTenantIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"No active certificate found for tenant {command.TenantId}.");
        signature.EnsureValid();

        // 3. Create Document entity
        var document = Document.Create(
            command.TenantId,
            command.Request.DocumentType,
            command.Request.IssuerInfo,
            command.Request.BuyerInfo);

        // Resolve strategy for this document type
        var strategy = _strategies.ResolveByDocumentType(document.DocumentType);

        // 4. Add items (not used for GuiaRemision — items live inside Destinatario)
        if (command.Request.DocumentType != DocumentType.GuiaRemision)
        {
            foreach (var itemDto in command.Request.Items)
            {
                var item = DocumentItem.Create(
                    document.Id,
                    itemDto.MainCode,
                    itemDto.Description,
                    itemDto.Quantity,
                    itemDto.UnitPrice,
                    itemDto.Discount,
                    itemDto.TaxRate,
                    itemDto.TaxCode,
                    itemDto.TaxPercentageCode,
                    itemDto.AuxiliaryCode,
                    itemDto.SustentoDocumentType,
                    itemDto.SustentoDocumentNumber,
                    itemDto.SustentoDocumentIssueDate,
                    itemDto.SustentoDocumentAuthNumber);
                document.AddItem(item);
            }
        }

        // 4b. Add destinatarios for GuiaRemision
        if (command.Request.DocumentType == DocumentType.GuiaRemision)
        {
            var destinatarioDtos = command.Request.Destinatarios;

            // Backward-compat shim: if no Destinatarios provided, build one from BuyerInfo + Items
            if (destinatarioDtos is null || destinatarioDtos.Count == 0)
            {
                var buyer = command.Request.BuyerInfo;
                var shimItems = command.Request.Items
                    .Select(i => new DestinatarioItemDto(i.MainCode, i.Description, i.Quantity))
                    .ToList();

                destinatarioDtos =
                [
                    new DestinatarioDto(
                        IdentificacionDestinatario: buyer.GetValueOrDefault("identificacionDestinatario", string.Empty),
                        RazonSocialDestinatario: buyer.GetValueOrDefault("razonSocialDestinatario", string.Empty),
                        DirDestinatario: buyer.GetValueOrDefault("dirDestinatario", string.Empty),
                        MotivoTraslado: buyer.GetValueOrDefault("motivoTraslado", string.Empty),
                        RucTransportista: buyer.GetValueOrDefault("rucTransportista", string.Empty),
                        Items: shimItems,
                        RutaEntrega: buyer.GetValueOrDefault("rutaEntrega"),
                        DocAduaneroUnico: buyer.GetValueOrDefault("docAduaneroUnico"),
                        CodEstabDestino: buyer.GetValueOrDefault("codEstabDestino"),
                        Rise: buyer.GetValueOrDefault("rise"))
                ];
            }

            foreach (var destDto in destinatarioDtos)
            {
                var destinatario = Destinatario.Create(
                    destDto.IdentificacionDestinatario,
                    destDto.RazonSocialDestinatario,
                    destDto.DirDestinatario,
                    destDto.MotivoTraslado,
                    destDto.RucTransportista,
                    destDto.RutaEntrega,
                    destDto.DocAduaneroUnico,
                    destDto.CodEstabDestino,
                    destDto.Rise);

                foreach (var itemDto in destDto.Items)
                {
                    var destItem = DestinatarioItem.Create(
                        itemDto.CodigoInterno,
                        itemDto.DescripcionDetalle,
                        itemDto.CantidadDetalle);
                    destinatario.AddItem(destItem);
                }

                document.AddDestinatario(destinatario);
            }
        }

        // 5. Validate using strategy
        var validationErrors = await strategy.ValidateDocumentAsync(document, cancellationToken);
        if (validationErrors.Count > 0)
        {
            throw new DocumentValidationException(
                $"Document validation failed: {string.Join("; ", validationErrors)}");
        }

        // 6. Generate XML (strategy delegates to IXmlGenerator internally)
        var xml = await strategy.BuildXmlAsync(document, cancellationToken);

        // Extract access key from generated XML — the strategy/XML generator embeds it
        // For now, set XML with a generated access key
        // The AccessKey is generated by the XML generator and embedded in the XML
        // We parse it or receive it from the strategy
        // In the current design, SetXmlContent requires an AccessKey, which is generated during XML building
        // The FacturaXmlBuilder generates the access key, so we need to extract it
        // For this handler, we rely on the fact that the XML generator returns XML containing the access key
        // We'll need to parse it or have the strategy return it
        // Simplification: generate access key here and pass to XML content
        document.SetXmlContent(xml, ExtractAccessKeyFromXml(xml));

        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.XmlGenerated, "XML generated successfully."),
            cancellationToken);

        _logger.LogInformation("XML generated for document {DocumentId}", document.Id);

        // 7. Sign XML
        var signedXml = await _documentSigner.SignDocumentAsync(
            xml, signature.CertificateData, signature.PasswordEncrypted, cancellationToken);
        document.SetSignedXml(signedXml);

        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.Signed, "Document signed with XAdES-BES."),
            cancellationToken);

        _logger.LogInformation("Document {DocumentId} signed successfully", document.Id);

        // 8. Send to SRI
        try
        {
            var sendResult = await _sriClient.SendDocumentAsync(signedXml, cancellationToken);
            document.MarkSentToSri();

            await _documentEventRepository.CreateAsync(
                DocumentEvent.Create(document.Id, command.TenantId, EventType.SentToSri,
                    $"Sent to SRI. Accepted: {sendResult.IsAccepted}. Status: {sendResult.Status}."),
                cancellationToken);

            if (!sendResult.IsAccepted)
            {
                var errorMsg = string.Join("; ", sendResult.Messages);
                document.Reject(errorMsg);

                await _documentEventRepository.CreateAsync(
                    DocumentEvent.Create(document.Id, command.TenantId, EventType.Rejected, $"SRI rejected: {errorMsg}"),
                    cancellationToken);

                _logger.LogWarning("Document {DocumentId} rejected by SRI: {Error}", document.Id, errorMsg);
            }
            else
            {
                // 9. Check authorization
                try
                {
                    var authResult = await _sriClient.CheckAuthorizationAsync(
                        document.AccessKey!.Value, cancellationToken);

                    if (authResult.IsAuthorized && authResult.AuthorizationNumber is not null
                        && authResult.AuthorizationDate.HasValue)
                    {
                        document.Authorize(authResult.AuthorizationNumber, authResult.AuthorizationDate.Value);

                        await _documentEventRepository.CreateAsync(
                            DocumentEvent.Create(document.Id, command.TenantId, EventType.Authorized,
                                $"Authorized by SRI. Auth#: {authResult.AuthorizationNumber}."),
                            cancellationToken);

                        _logger.LogInformation("Document {DocumentId} authorized by SRI", document.Id);

                        // 10. Generate RIDE PDF (fire-and-forget in background, but we log the event)
                        try
                        {
                            await strategy.BuildRidePdfAsync(document, cancellationToken);
                            await _documentEventRepository.CreateAsync(
                                DocumentEvent.Create(document.Id, command.TenantId, EventType.PdfGenerated, "RIDE PDF generated."),
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to generate RIDE PDF for document {DocumentId}", document.Id);
                        }
                    }
                    else
                    {
                        // SRI accepted but not yet authorized — schedule retry
                        document.ScheduleRetry(DateTime.UtcNow.AddMinutes(5));

                        await _documentEventRepository.CreateAsync(
                            DocumentEvent.Create(document.Id, command.TenantId, EventType.RetryScheduled,
                                "SRI accepted but not yet authorized. Retry scheduled."),
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogError(ex, "SRI authorization check failed for document {DocumentId}: {Error}",
                        document.Id, ex.Message);

                    document.MarkFailed($"SRI authorization check failed: {ex.Message}");

                    await _documentEventRepository.CreateAsync(
                        DocumentEvent.Create(document.Id, command.TenantId, EventType.Failed,
                            $"SRI authorization check failed: {ex.GetType().Name} — {ex.Message}"),
                        cancellationToken);

                    // Persist document with Failed status before re-throwing
                    await _documentRepository.CreateAsync(document, cancellationToken);
                    var failedUsageRecord = UsageRecord.Create(command.TenantId, document.Id, document.DocumentType);
                    await _usageRecordRepository.CreateAsync(failedUsageRecord, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);

                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "SRI send failed for document {DocumentId}: {Error}",
                document.Id, ex.Message);

            document.MarkFailed($"SRI service error: {ex.Message}");

            await _documentEventRepository.CreateAsync(
                DocumentEvent.Create(document.Id, command.TenantId, EventType.Failed,
                    $"SRI send failed: {ex.GetType().Name} — {ex.Message}"),
                cancellationToken);

            // Persist document with Failed status before re-throwing
            await _documentRepository.CreateAsync(document, cancellationToken);
            var failedUsageRecord = UsageRecord.Create(command.TenantId, document.Id, document.DocumentType);
            await _usageRecordRepository.CreateAsync(failedUsageRecord, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            throw;
        }

        // 11. Persist document and usage record
        await _documentRepository.CreateAsync(document, cancellationToken);

        var usageRecord = UsageRecord.Create(command.TenantId, document.Id, document.DocumentType);
        await _usageRecordRepository.CreateAsync(usageRecord, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return MapToResponse(document);
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

    /// <summary>
    /// Extracts the 49-digit access key from the generated XML.
    /// The access key is in the claveAcceso element.
    /// </summary>
    private static Domain.ValueObjects.AccessKey ExtractAccessKeyFromXml(string xml)
    {
        // Simple extraction — the XML contains <claveAcceso>49digits</claveAcceso>
        var startTag = "<claveAcceso>";
        var endTag = "</claveAcceso>";
        var startIndex = xml.IndexOf(startTag, StringComparison.Ordinal);
        if (startIndex < 0)
            throw new DocumentValidationException("Generated XML does not contain claveAcceso element.");

        startIndex += startTag.Length;
        var endIndex = xml.IndexOf(endTag, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            throw new DocumentValidationException("Generated XML has malformed claveAcceso element.");

        var accessKeyValue = xml[startIndex..endIndex];
        return new Domain.ValueObjects.AccessKey(accessKeyValue);
    }
}
