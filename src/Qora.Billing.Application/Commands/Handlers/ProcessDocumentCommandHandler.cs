using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Extensions;
using Qora.Billing.Application.Logging;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

/// <summary>
/// Handler del pipeline principal: valida → crea Document → genera XML → firma → envía al SRI → genera RIDE → guarda.
///
/// Idempotencia (II-1/II-2):
///   - Si el request trae Idempotency-Key, se reproduce la respuesta original ante un reintento con el
///     mismo cuerpo (replay) y se rechaza con 422 si la clave se reusa con un cuerpo distinto.
///   - El constraint de identidad de negocio (tenant, tipo, estab, ptoEmi, secuencial) deduplica aun sin
///     Idempotency-Key: ante violación de unicidad se devuelve el comprobante existente, nunca un 500.
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
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IdempotencySettings _idempotencySettings;
    private readonly EmissionOptions _emissionOptions;
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
        IIdempotencyStore idempotencyStore,
        IUnitOfWork unitOfWork,
        IOptions<IdempotencySettings> idempotencySettings,
        IOptions<EmissionOptions> emissionOptions,
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
        _idempotencyStore = idempotencyStore;
        _unitOfWork = unitOfWork;
        _idempotencySettings = idempotencySettings.Value;
        _emissionOptions = emissionOptions.Value;
        _logger = logger;
    }

    public async Task<DocumentResponse> Handle(ProcessDocumentCommand command, CancellationToken cancellationToken)
    {
        // 0. Idempotency-Key (II-1): replay determinista sin depender de claveAcceso.
        var requestHash = IdempotencyHasher.ComputeRequestHash(command.Request);
        IdempotencyKey? idempotencyEntry = null;

        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var replay = await TryReplayOrStartAsync(command, requestHash, cancellationToken);
            if (replay.Replayed)
            {
                return replay.Response!;
            }
            idempotencyEntry = replay.Entry;
        }

        var response = await IssueDocumentAsync(command, cancellationToken);

        // Persiste el snapshot para futuros replays (II-1). Sólo cuando hubo Idempotency-Key.
        if (idempotencyEntry is not null)
        {
            var snapshot = JsonSerializer.Serialize(response);
            idempotencyEntry.Complete(snapshot, response.Id);
            await _idempotencyStore.CompleteAsync(idempotencyEntry, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Resuelve la idempotencia por clave: si ya existe un registro completado con el MISMO hash devuelve
    /// el snapshot (replay); con hash distinto lanza 422 (reuso de clave con payload distinto). Si no
    /// existe, inserta el lock (status=in_progress) y devuelve la entrada para completarla tras emitir.
    /// Una carrera concurrente (segundo insert con misma clave) se trata como conflicto/in-progress.
    /// </summary>
    private async Task<(bool Replayed, DocumentResponse? Response, IdempotencyKey? Entry)> TryReplayOrStartAsync(
        ProcessDocumentCommand command, string requestHash, CancellationToken cancellationToken)
    {
        var key = command.IdempotencyKey!;

        var existing = await _idempotencyStore.FindAsync(key, cancellationToken);
        if (existing is not null)
        {
            return EvaluateExisting(existing, requestHash);
        }

        var expiresAt = DateTime.UtcNow.AddDays(_idempotencySettings.RetentionDays);
        var entry = IdempotencyKey.Start(command.TenantId, key, requestHash, expiresAt);

        var started = await _idempotencyStore.TryStartAsync(entry, cancellationToken);
        if (started)
        {
            return (false, null, entry);
        }

        // Otra petición concurrente ganó la carrera del insert. Releemos el registro y lo evaluamos.
        var raced = await _idempotencyStore.FindAsync(key, cancellationToken);
        if (raced is not null)
        {
            return EvaluateExisting(raced, requestHash);
        }

        // Estado inesperado (registro desaparecido): tratamos como conflicto para no reemitir.
        throw new IdempotencyConflictException(
            $"La Idempotency-Key '{key}' está siendo procesada concurrentemente. Reintente más tarde.");
    }

    /// <summary>
    /// Evalúa un registro de idempotencia ya existente contra el hash del request actual.
    /// </summary>
    private (bool Replayed, DocumentResponse? Response, IdempotencyKey? Entry) EvaluateExisting(
        IdempotencyKey existing, string requestHash)
    {
        if (existing.RequestHash != requestHash)
        {
            throw new IdempotencyConflictException(
                $"La Idempotency-Key '{existing.Key}' ya se usó con un cuerpo de request distinto.");
        }

        if (existing.IsCompleted && existing.ResponseSnapshot is not null)
        {
            var stored = JsonSerializer.Deserialize<DocumentResponse>(existing.ResponseSnapshot)
                ?? throw new BillingDomainException("El snapshot de idempotencia almacenado es inválido.");
            _logger.LogInformation(
                "Replay idempotente para la clave {Key} → documento {DocumentId}", existing.Key, stored.Id);
            return (true, stored, null);
        }

        // Existe pero aún en curso (in_progress) con el mismo hash → conflicto: hay una emisión en vuelo.
        throw new IdempotencyConflictException(
            $"La Idempotency-Key '{existing.Key}' está siendo procesada. Reintente cuando finalice.");
    }

    /// <summary>
    /// Flujo de emisión con 5 checkpoints persistentes (design §3.3, D1-D5). Corre dentro de la
    /// transacción ambiental abierta por <c>TenantContextMiddleware</c>:
    /// <list type="number">
    ///   <item>C1: pre-reserva — <c>SELECT ... FOR UPDATE</c> del MAX(secuencial) + INSERT del Draft.</item>
    ///   <item>C2: XmlGenerated — genera y persiste el XML + claveAcceso.</item>
    ///   <item>C3: Signed — firma y persiste el XML firmado.</item>
    ///   <item>C4: SentToSri — envía al SRI y persiste el estado (persistencia temprana, fix gap 5).</item>
    ///   <item>C5: Authorized/Rejected/PendingRetry — verifica autorización y persiste el estado final.</item>
    /// </list>
    /// Las fallas transitorias del SRI (clasificadas por <see cref="SriExceptionClassifier"/>) dejan el
    /// documento en su último checkpoint persistido (Signed/SentToSri) y relanzan; el reconciliador lo
    /// recoge. Ver REQ-EMI-001..015.
    /// </summary>
    private async Task<DocumentResponse> IssueDocumentAsync(
        ProcessDocumentCommand command, CancellationToken cancellationToken)
    {
        var (document, tenant, signature, strategy) =
            await BuildAndValidateDocumentAsync(command, cancellationToken);

        // ── C1: pre-reserva (lock sobre el MAX(secuencial) + INSERT Draft) ─────────────────────
        // El lock FOR UPDATE serializa emisiones concurrentes de la MISMA identidad de negocio y se
        // libera al commit/rollback de la transacción ambiental (design D1, D3.a). El modo de
        // numeración es por-tenant (Tenant.AutoGenerateSecuencial, design D1/D2): el flag global de
        // EmissionOptions queda demoted a fallback de sistema. En modo CLIENTE el secuencial lo provee
        // quien emite (monotonicidad MAX+1); en modo AUTO el servidor asigna MAX+1 bajo lock. En ambos
        // casos tomamos el lock para evitar la doble emisión (gap 2 del proposal).
        if (tenant.AutoGenerateSecuencial)
        {
            // ── Modo AUTO: el servidor es autoritativo sobre el secuencial (design D3/D4/D5/D6). ──
            // Conflicto (D6): si el cliente además envió un secuencial, rechazamos 422 antes de emitir
            // (no lo sobre-escribimos en silencio). El validador no puede enforzar esto: desconoce el
            // modo del tenant.
            if (document.Secuencial is not null)
            {
                throw new DocumentValidationException(
                    "El tenant emite en modo automático (server-side): no debe enviar 'secuencial'. " +
                    "Se recibió un secuencial en el request.");
            }

            return await IssueAutoSecuencialAsync(command, document, signature, strategy, cancellationToken);
        }

        var currentMax = await _documentRepository.GetMaxSecuencialWithLockAsync(
            command.TenantId, document.DocumentType,
            document.Estab ?? string.Empty, document.PtoEmision ?? string.Empty, cancellationToken);

        // ── Modo CLIENTE (default): comportamiento INALTERADO por el modo AUTO (design D4, guard R7). ──
        if (document.Secuencial is not null)
        {
            // Monotonicidad: el secuencial provisto debe ser EXACTAMENTE MAX+1 (o "000000001" en la
            // primera emisión del tuple). Se rechaza tanto el hueco (provisto > MAX+1) como el
            // duplicado/regresión (provisto <= MAX) (REQ-EMI-008/010/012, S-EMI-003). La comparación
            // se hace sobre la forma string de 9 dígitos con padding de ceros.
            var expected = ComputeNextSecuencial(currentMax);
            var provided = TryParseSecuencial(document.Secuencial, out var providedNumeric)
                ? providedNumeric.ToString("D9")
                : document.Secuencial;

            if (!string.Equals(provided, expected, StringComparison.Ordinal))
            {
                _logger.LogEmissionMonotonicityViolation(document.Id, expected, document.Secuencial);
                throw new DocumentValidationException(
                    $"Secuencial fuera de orden: esperaba {expected}, recibió {provided}");
            }
        }

        try
        {
            await _documentRepository.CreateAsync(document, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C1
        }
        catch (DuplicateBusinessIdentityException)
        {
            // CLIENTE: una colisión de identidad de negocio deduplica al comprobante existente (II-2).
            _logger.LogEmissionPreReservationDuplicate(document.Id, document.Secuencial);
            return await DedupeAndReturnAsync(document, cancellationToken);
        }

        _logger.LogEmissionPreReserved(document.Id, document.Secuencial ?? "(auto)");

        // C2..C5 son idénticos en ambos modos (la pre-reserva C1 es lo único que difiere).
        return await RunEmissionPipelineAsync(command, document, signature, strategy, cancellationToken);
    }

    /// <summary>
    /// Modo AUTO (design D3/D5): el servidor asigna el secuencial server-side. Tras tomar el lock
    /// FOR UPDATE que devuelve <c>currentMax</c>, computa MAX+1 y lo asigna en la columna Y en
    /// <c>IssuerInfo["secuencial"]</c> (AssignSecuencial) ANTES del INSERT C1 y ANTES de que la
    /// estrategia construya la clave de acceso (C2). Una emisión AUTO concurrente sobre la misma
    /// identidad de negocio son comprobantes DISTINTOS, ambos en MAX+1: el perdedor recibe
    /// <see cref="DuplicateBusinessIdentityException"/> del unique constraint y reintenta (re-lock +
    /// recomputar MAX+1 + re-asignar). NUNCA deduplica (a diferencia del modo CLIENTE). El bound de 5
    /// acota la contención patológica; la terminación está garantizada porque cada reintento ve un MAX
    /// committed estrictamente mayor (el ganador ya hizo commit).
    /// </summary>
    private async Task<DocumentResponse> IssueAutoSecuencialAsync(
        ProcessDocumentCommand command,
        Document document,
        ElectronicSignature signature,
        IDocumentTypeStrategy strategy,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var currentMax = await _documentRepository.GetMaxSecuencialWithLockAsync(
                command.TenantId, document.DocumentType,
                document.Estab ?? string.Empty, document.PtoEmision ?? string.Empty, cancellationToken);

            // Asigna MAX+1 por el único camino de escritura (columna + issuer["secuencial"]) ANTES de
            // C1 y de que C2 construya la clave de acceso de 49 dígitos (design D3).
            var next = ComputeNextSecuencial(currentMax);
            document.AssignSecuencial(next);

            try
            {
                await _documentRepository.CreateAsync(document, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C1 (AUTO)
            }
            catch (DuplicateBusinessIdentityException)
            {
                // AUTO: NO deduplica. El perdedor de la carrera recomputa MAX+1 y reintenta.
                _logger.LogEmissionPreReservationDuplicate(document.Id, document.Secuencial);
                if (attempt == maxAttempts)
                {
                    _logger.LogWarning(
                        "Secuencial AUTO agotó {MaxAttempts} reintentos para el documento {DocumentId} (estab {Estab}, ptoEmi {PtoEmi}).",
                        maxAttempts, document.Id, document.Estab, document.PtoEmision);
                    throw new SecuencialExhaustedException(
                        $"No se pudo asignar un secuencial tras {maxAttempts} intentos por contención. Reintente.");
                }
                continue;
            }

            _logger.LogEmissionPreReserved(document.Id, document.Secuencial ?? "(auto)");

            return await RunEmissionPipelineAsync(command, document, signature, strategy, cancellationToken);
        }

        // Inalcanzable: el bucle o retorna en el insert exitoso o lanza en el último intento.
        throw new SecuencialExhaustedException(
            $"No se pudo asignar un secuencial tras {maxAttempts} intentos por contención. Reintente.");
    }

    /// <summary>
    /// Pipeline de emisión C2..C5 (XML → firma → envío al SRI → autorización), compartido por los modos
    /// CLIENTE y AUTO. Se ejecuta tras una pre-reserva C1 exitosa (el documento ya está persistido como
    /// Draft con su secuencial). Corre dentro de la transacción ambiental.
    /// </summary>
    private async Task<DocumentResponse> RunEmissionPipelineAsync(
        ProcessDocumentCommand command,
        Document document,
        ElectronicSignature signature,
        IDocumentTypeStrategy strategy,
        CancellationToken cancellationToken)
    {
        // ── C2: XmlGenerated ───────────────────────────────────────────────────────────────────
        var (xml, accessKey) = await strategy.BuildXmlAsync(document, cancellationToken);
        document.SetXmlContent(xml, accessKey);
        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.XmlGenerated, "XML generado exitosamente."),
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C2

        // ── C3: Signed ─────────────────────────────────────────────────────────────────────────
        var signedXml = await _documentSigner.SignDocumentAsync(
            xml, signature.CertificateData, signature.PasswordEncrypted, cancellationToken);
        document.SetSignedXml(signedXml);
        await _documentEventRepository.CreateAsync(
            DocumentEvent.Create(document.Id, command.TenantId, EventType.Signed, "Documento firmado con XAdES-BES."),
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C3

        // ── C4: SentToSri (persistencia temprana) + C5: autorización ───────────────────────────
        try
        {
            var sendResult = await _sriClient.SendDocumentAsync(signedXml, cancellationToken);
            document.MarkSentToSri();
            await _documentEventRepository.CreateAsync(
                DocumentEvent.Create(document.Id, command.TenantId, EventType.SentToSri,
                    $"Enviado al SRI. Aceptado: {sendResult.IsAccepted}. Estado: {sendResult.Status}."),
                cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C4
            _logger.LogEmissionSentToSriPersisted(document.Id);

            if (!sendResult.IsAccepted)
            {
                var errorMsg = string.Join("; ", sendResult.Messages);
                document.Reject(errorMsg);
                await _documentEventRepository.CreateAsync(
                    DocumentEvent.Create(document.Id, command.TenantId, EventType.Rejected, $"SRI rechazó el documento: {errorMsg}"),
                    cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C5.reject
                _logger.LogWarning("Document {DocumentId} rejected by SRI: {Error}", document.Id, errorMsg);
                return MapToResponse(document);
            }

            try
            {
                var authResult = await _sriClient.CheckAuthorizationAsync(document.AccessKey!.Value, cancellationToken);

                if (authResult.IsAuthorized && authResult.AuthorizationNumber is not null && authResult.AuthorizationDate.HasValue)
                {
                    document.Authorize(authResult.AuthorizationNumber, authResult.AuthorizationDate.Value);
                    await _documentEventRepository.CreateAsync(
                        DocumentEvent.Create(document.Id, command.TenantId, EventType.Authorized,
                            $"Autorizado por SRI. N° autorización: {authResult.AuthorizationNumber}."),
                        cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C5.authorized

                    try
                    {
                        await strategy.BuildRidePdfAsync(document, cancellationToken);
                        // El RIDE no se persiste (se genera on-demand); marcamos ride_generated_at como
                        // señal de éxito. Si esta generación best-effort falla (catch de abajo), el
                        // documento queda con ride_generated_at NULL y el RidePdfRetryService lo regenera.
                        document.MarkRideGenerated();
                        await _documentEventRepository.CreateAsync(
                            DocumentEvent.Create(document.Id, command.TenantId, EventType.PdfGenerated, "RIDE PDF generado."),
                            cancellationToken);
                        await _unitOfWork.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate RIDE PDF for document {DocumentId}", document.Id);
                    }
                }
                else
                {
                    document.ScheduleRetry(DateTime.UtcNow.AddMinutes(5));
                    await _documentEventRepository.CreateAsync(
                        DocumentEvent.Create(document.Id, command.TenantId, EventType.RetryScheduled,
                            "SRI aceptó el documento pero aún no está autorizado. Reintento programado."),
                        cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken); // ← C5.retry
                }
            }
            catch (Exception ex) when (SriExceptionClassifier.IsSriTransientOrCircuitOpen(ex))
            {
                // El doc YA está persistido como SentToSri (C4). El reconciliador lo recogerá (REQ-EMI-002).
                _logger.LogEmissionCircuitOpenCaught(ex, document.Id, ex.GetType().Name);
                throw;
            }
        }
        catch (Exception ex) when (SriExceptionClassifier.IsSriTransientOrCircuitOpen(ex))
        {
            // El doc YA está persistido en Signed (C3) o SentToSri (C4). El reconciliador lo recogerá.
            _logger.LogEmissionCircuitOpenCaught(ex, document.Id, ex.GetType().Name);
            throw;
        }

        return MapToResponse(document);
    }

    private static bool TryParseSecuencial(string value, out long parsed)
        => long.TryParse(value.Trim(), out parsed);

    private static string ComputeNextSecuencial(string? currentMax)
    {
        if (currentMax is null || !TryParseSecuencial(currentMax, out var numeric))
            return "000000001";
        return (numeric + 1).ToString("D9");
    }

    /// <summary>
    /// Deduplica ante una violación de identidad de negocio en C1: devuelve el comprobante existente
    /// del tenant (REQ-EMI-006) en lugar de propagar un error.
    /// </summary>
    private async Task<DocumentResponse> DedupeAndReturnAsync(Document document, CancellationToken cancellationToken)
    {
        var existing = await _documentRepository.GetByBusinessIdentityAsync(
            document.DocumentType,
            document.Estab ?? string.Empty,
            document.PtoEmision ?? string.Empty,
            document.Secuencial ?? string.Empty,
            cancellationToken)
            ?? throw new BillingDomainException(
                "Conflicto de identidad de negocio pero no se encontró el comprobante existente.");
        return MapToResponse(existing);
    }

    /// <summary>
    /// Valida tenant + certificado y construye la entidad <see cref="Document"/> con sus ítems o
    /// destinatarios, ejecutando la validación de la estrategia.
    /// </summary>
    private async Task<(Document Document, Tenant Tenant, ElectronicSignature Signature, IDocumentTypeStrategy Strategy)>
        BuildAndValidateDocumentAsync(ProcessDocumentCommand command, CancellationToken cancellationToken)
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

        return (document, tenant, signature, strategy);
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
            document.ProcessedAt,
            document.Secuencial,
            document.Estab,
            document.PtoEmision);
    }

}
