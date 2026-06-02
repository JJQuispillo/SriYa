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
/// Handler del pipeline principal: valida → crea Document → genera XML → firma → envía al SRI → genera RIDE → guarda.
/// </summary>
public class ProcessDocumentCommandHandler : IRequestHandler<ProcessDocumentCommand, DocumentResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IElectronicSignatureRepository _signatureRepository;
    private readonly IDocumentEventRepository _documentEventRepository;
    private readonly ISriTaxCodeRepository _sriTaxCodeRepository;
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
        ISriTaxCodeRepository sriTaxCodeRepository,
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
        _sriTaxCodeRepository = sriTaxCodeRepository;
        _strategies = strategies;
        _documentSigner = documentSigner;
        _sriClient = sriClient;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<DocumentResponse> Handle(ProcessDocumentCommand command, CancellationToken cancellationToken)
    {
        // 1. Valida que el tenant exista y esté activo
        var tenant = await _tenantRepository.GetByIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"Tenant {command.TenantId} no encontrado.");
        tenant.EnsureActive();

        // 2. Obtiene el certificado activo
        var signature = await _signatureRepository.GetActiveByTenantIdAsync(command.TenantId, cancellationToken)
            ?? throw new BillingDomainException($"No se encontró un certificado activo para el tenant {command.TenantId}.");
        signature.EnsureValid();

        // 3. Crea la entidad Document
        var document = Document.Create(
            command.TenantId,
            command.Request.TipoDocumento,
            command.Request.Emisor,
            command.Request.Comprador);

        // Resuelve la estrategia para este tipo de documento
        var strategy = _strategies.ResolveByDocumentType(document.DocumentType);

        // 4. Agrega los ítems (no se usa para GuiaRemision — los ítems viven dentro del Destinatario)
        if (command.Request.TipoDocumento != DocumentType.GuiaRemision)
        {
            foreach (var itemDto in command.Request.Detalles)
            {
                // Deriva el TaxRate de la tabla de referencia del SRI en lugar de confiar en la entrada del llamador
                var taxCode = await _sriTaxCodeRepository.FindAsync(itemDto.CodigoImpuesto, itemDto.CodigoPorcentaje, cancellationToken)
                    ?? throw new BillingDomainException(
                        $"Código de impuesto '{itemDto.CodigoImpuesto}/{itemDto.CodigoPorcentaje}' no está registrado.");

                var item = DocumentItem.Create(
                    document.Id,
                    itemDto.CodigoPrincipal,
                    itemDto.Descripcion,
                    itemDto.Cantidad,
                    itemDto.PrecioUnitario,
                    itemDto.Descuento,
                    taxCode.Rate,
                    itemDto.CodigoImpuesto,
                    itemDto.CodigoPorcentaje,
                    itemDto.CodigoAuxiliar,
                    itemDto.TipoDocSustento,
                    itemDto.NumDocSustento,
                    itemDto.FechaEmisionDocSustento,
                    itemDto.NumAutDocSustento);
                document.AddItem(item);
            }
        }

        // 4b. Agrega los destinatarios para GuiaRemision
        if (command.Request.TipoDocumento == DocumentType.GuiaRemision)
        {
            // El request tipado siempre provee Destinatarios (≥1, validado en el endpoint).
            var destinatarioDtos = command.Request.Destinatarios ?? [];

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

        // 5. Valida usando la estrategia
        var validationErrors = await strategy.ValidateDocumentAsync(document, cancellationToken);
        if (validationErrors.Count > 0)
        {
            throw new DocumentValidationException(
                $"La validación del documento falló: {string.Join("; ", validationErrors)}");
        }

        // 6. Genera el XML (la estrategia delega internamente en IXmlGenerator)
        var xml = await strategy.BuildXmlAsync(document, cancellationToken);

        // Extrae la clave de acceso del XML generado — la estrategia/generador de XML la incrusta
        // Por ahora, establece el XML con una clave de acceso generada
        // La AccessKey la genera el generador de XML y se incrusta en el XML
        // La parseamos o la recibimos desde la estrategia
        // En el diseño actual, SetXmlContent requiere una AccessKey, que se genera durante la construcción del XML
        // El FacturaXmlBuilder genera la clave de acceso, por lo que necesitamos extraerla
        // En este handler, nos apoyamos en que el generador de XML devuelve un XML que contiene la clave de acceso
        // Necesitaremos parsearla o hacer que la estrategia la devuelva
        // Simplificación: generar la clave de acceso aquí y pasarla al contenido del XML
        document.SetXmlContent(xml, ExtractAccessKeyFromXml(xml));

        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.XmlGenerated, "XML generado exitosamente."),
            cancellationToken);

        _logger.LogInformation("XML generated for document {DocumentId}", document.Id);

        // 7. Firma el XML
        var signedXml = await _documentSigner.SignDocumentAsync(
            xml, signature.CertificateData, signature.PasswordEncrypted, cancellationToken);
        document.SetSignedXml(signedXml);

        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.Signed, "Documento firmado con XAdES-BES."),
            cancellationToken);

        _logger.LogInformation("Document {DocumentId} signed successfully", document.Id);

        // 8. Envía al SRI
        try
        {
            var sendResult = await _sriClient.SendDocumentAsync(signedXml, cancellationToken);
            document.MarkSentToSri();

            await _documentEventRepository.CreateAsync(
                DocumentEvent.Create(document.Id, command.TenantId, EventType.SentToSri,
                    $"Enviado al SRI. Aceptado: {sendResult.IsAccepted}. Estado: {sendResult.Status}."),
                cancellationToken);

            if (!sendResult.IsAccepted)
            {
                var errorMsg = string.Join("; ", sendResult.Messages);
                document.Reject(errorMsg);

                await _documentEventRepository.CreateAsync(
                    DocumentEvent.Create(document.Id, command.TenantId, EventType.Rejected, $"SRI rechazó el documento: {errorMsg}"),
                    cancellationToken);

                _logger.LogWarning("Document {DocumentId} rejected by SRI: {Error}", document.Id, errorMsg);
            }
            else
            {
                // 9. Verifica la autorización
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
                                $"Autorizado por SRI. N° autorización: {authResult.AuthorizationNumber}."),
                            cancellationToken);

                        _logger.LogInformation("Document {DocumentId} authorized by SRI", document.Id);

                        // 10. Genera el PDF del RIDE (fire-and-forget en segundo plano, pero registramos el evento)
                        try
                        {
                            await strategy.BuildRidePdfAsync(document, cancellationToken);
                            await _documentEventRepository.CreateAsync(
                                DocumentEvent.Create(document.Id, command.TenantId, EventType.PdfGenerated, "RIDE PDF generado."),
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to generate RIDE PDF for document {DocumentId}", document.Id);
                        }
                    }
                    else
                    {
                        // El SRI aceptó pero aún no autorizó — programa un reintento
                        document.ScheduleRetry(DateTime.UtcNow.AddMinutes(5));

                        await _documentEventRepository.CreateAsync(
                            DocumentEvent.Create(document.Id, command.TenantId, EventType.RetryScheduled,
                                "SRI aceptó el documento pero aún no está autorizado. Reintento programado."),
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogError(ex, "SRI authorization check failed for document {DocumentId}: {Error}",
                        document.Id, ex.Message);

                    document.MarkFailed($"Error al verificar la autorización en SRI: {ex.Message}");

                    await _documentEventRepository.CreateAsync(
                        DocumentEvent.Create(document.Id, command.TenantId, EventType.Failed,
                            $"Error al verificar la autorización en SRI: {ex.GetType().Name} — {ex.Message}"),
                        cancellationToken);

                    // Persiste el documento con estado Failed antes de relanzar la excepción
                    await _documentRepository.CreateAsync(document, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);

                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "SRI send failed for document {DocumentId}: {Error}",
                document.Id, ex.Message);

            document.MarkFailed($"Error en el servicio SRI: {ex.Message}");

            await _documentEventRepository.CreateAsync(
                DocumentEvent.Create(document.Id, command.TenantId, EventType.Failed,
                    $"Error al enviar al SRI: {ex.GetType().Name} — {ex.Message}"),
                cancellationToken);

            // Persiste el documento con estado Failed antes de relanzar la excepción
            await _documentRepository.CreateAsync(document, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            throw;
        }

        // 11. Persiste el documento
        await _documentRepository.CreateAsync(document, cancellationToken);
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
    /// Extrae la clave de acceso de 49 dígitos del XML generado.
    /// La clave de acceso está en el elemento claveAcceso.
    /// </summary>
    private static Domain.ValueObjects.AccessKey ExtractAccessKeyFromXml(string xml)
    {
        // Extracción simple — el XML contiene <claveAcceso>49dígitos</claveAcceso>
        var startTag = "<claveAcceso>";
        var endTag = "</claveAcceso>";
        var startIndex = xml.IndexOf(startTag, StringComparison.Ordinal);
        if (startIndex < 0)
            throw new DocumentValidationException("El XML generado no contiene el elemento claveAcceso.");

        startIndex += startTag.Length;
        var endIndex = xml.IndexOf(endTag, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            throw new DocumentValidationException("El XML generado tiene un elemento claveAcceso malformado.");

        var accessKeyValue = xml[startIndex..endIndex];
        return new Domain.ValueObjects.AccessKey(accessKeyValue);
    }
}
