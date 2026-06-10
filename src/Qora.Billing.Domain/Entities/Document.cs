using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Events;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Domain.Entities;

public class Document : BaseEntity
{
    public Guid TenantId { get; private set; }
    public Enums.DocumentType DocumentType { get; private set; }
    public AccessKey? AccessKey { get; private set; }

    /// <summary>
    /// Código de establecimiento del SRI (3 dígitos, ej. "001"). Identidad de negocio.
    /// Promovido a columna real desde IssuerInfo["estab"] para el índice/constraint de unicidad.
    /// </summary>
    public string? Estab { get; private set; }

    /// <summary>
    /// Punto de emisión del SRI (3 dígitos, ej. "001"). Identidad de negocio.
    /// Promovido a columna real desde IssuerInfo["ptoEmi"].
    /// </summary>
    public string? PtoEmision { get; private set; }

    /// <summary>
    /// Secuencial del SRI (9 dígitos, ej. "000000001"). Identidad de negocio.
    /// Promovido a columna real desde IssuerInfo["secuencial"].
    /// </summary>
    public string? Secuencial { get; private set; }

    public DocumentStatus Status { get; private set; }
    public string? XmlContent { get; private set; }
    public string? SignedXmlContent { get; private set; }
    public string? SriAuthorizationNumber { get; private set; }
    public DateTime? SriAuthorizationDate { get; private set; }

    /// <summary>
    /// Información del emisor almacenada como diccionario listo para JSON, para dar flexibilidad entre tipos de documento.
    /// </summary>
    public Dictionary<string, string> IssuerInfo { get; private set; } = new();

    /// <summary>
    /// Información del comprador/receptor almacenada como diccionario listo para JSON.
    /// </summary>
    public Dictionary<string, string> BuyerInfo { get; private set; } = new();

    private readonly List<DocumentItem> _items = [];
    public IReadOnlyList<DocumentItem> Items => _items.AsReadOnly();

    private readonly List<Destinatario> _destinatarios = [];
    public IReadOnlyCollection<Destinatario> Destinatarios => _destinatarios.AsReadOnly();

    public string? ErrorMessage { get; private set; }
    public int RetryCount { get; private set; }
    public DateTime? NextRetryAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }

    /// <summary>
    /// Marca temporal de cuándo se generó exitosamente el RIDE PDF del documento. El RIDE no se
    /// persiste (se genera on-demand), por lo que esta columna es el ÚNICO marcador de que la
    /// generación del RIDE en la emisión tuvo éxito. Nullable: un documento autorizado con
    /// <c>RideGeneratedAt IS NULL</c> es candidato a regeneración por <c>RidePdfRetryService</c>
    /// (red de seguridad del best-effort que se traga la excepción en la emisión).
    /// </summary>
    public DateTime? RideGeneratedAt { get; private set; }

    /// <summary>
    /// Número de intentos de generación del RIDE realizados por <c>RidePdfRetryService</c>. Acota
    /// el reintento: al alcanzar el máximo configurado el barrido deja de reintentar ese documento.
    /// </summary>
    public int RideRetryCount { get; private set; }

    /// <summary>
    /// Marca de borrado lógico (soft-delete). Cuando NO es nulo el documento se considera retirado del
    /// ciclo de vida operativo, pero conserva los campos fiscales obligatorios (PL-2). Las lecturas
    /// normales lo excluyen vía el global query filter; los caminos de retención lo pueden inspeccionar
    /// con IgnoreQueryFilters.
    /// </summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>true cuando el documento fue anonimizado (PII del comprador redactada).</summary>
    public bool IsAnonymized { get; private set; }

    private Document() { } // EF Core

    public static Document Create(
        Guid tenantId,
        Enums.DocumentType documentType,
        Dictionary<string, string> issuerInfo,
        Dictionary<string, string> buyerInfo)
    {
        var document = new Document
        {
            TenantId = tenantId,
            DocumentType = documentType,
            Status = DocumentStatus.Draft,
            IssuerInfo = issuerInfo ?? throw new ArgumentNullException(nameof(issuerInfo)),
            BuyerInfo = buyerInfo ?? throw new ArgumentNullException(nameof(buyerInfo)),
            RetryCount = 0
        };

        // Promover la identidad de negocio (estab/ptoEmi/secuencial) a columnas reales desde IssuerInfo.
        // Estos campos los provee quien emite y están presentes al crear; el resto de campos del sistema
        // (claveAcceso, ambiente, etc.) se rellenan más tarde en la estrategia.
        document.SyncBusinessIdentityFromIssuerInfo();

        document.AddDomainEvent(new DocumentCreatedEvent(document.Id, tenantId, documentType));
        return document;
    }

    /// <summary>
    /// Copia estab/ptoEmi/secuencial desde el diccionario IssuerInfo hacia las columnas reales.
    /// Idempotente: sólo asigna cuando la clave correspondiente existe en IssuerInfo.
    /// </summary>
    public void SyncBusinessIdentityFromIssuerInfo()
    {
        if (IssuerInfo.TryGetValue("estab", out var estab))
            Estab = estab;
        if (IssuerInfo.TryGetValue("ptoEmi", out var ptoEmi))
            PtoEmision = ptoEmi;
        if (IssuerInfo.TryGetValue("secuencial", out var secuencial))
            Secuencial = secuencial;
    }

    /// <summary>
    /// Asigna el secuencial generado server-side (modo AUTO) por un único camino de escritura: fija la
    /// columna <see cref="Secuencial"/> Y escribe <c>IssuerInfo["secuencial"]</c> de forma consistente,
    /// de modo que la estrategia que construye la clave de acceso de 49 dígitos lea los 9 dígitos correctos.
    /// Deliberadamente NO re-ejecuta <see cref="SyncBusinessIdentityFromIssuerInfo"/> (que copia en sentido
    /// inverso, dict→columna): aquí la fuente de verdad es el valor asignado, no el diccionario.
    /// </summary>
    /// <param name="secuencial">Secuencial de 9 dígitos (ej. "000000001").</param>
    public void AssignSecuencial(string secuencial)
    {
        if (string.IsNullOrWhiteSpace(secuencial))
            throw new ArgumentException("El secuencial asignado no puede ser nulo ni vacío.", nameof(secuencial));

        Secuencial = secuencial;
        IssuerInfo["secuencial"] = secuencial;
        SetUpdatedAt();
    }

    public void AddItem(DocumentItem item)
    {
        EnsureStatus(DocumentStatus.Draft);
        _items.Add(item);
        SetUpdatedAt();
    }

    public void AddDestinatario(Destinatario destinatario)
    {
        if (destinatario is null) throw new ArgumentNullException(nameof(destinatario));
        EnsureStatus(DocumentStatus.Draft);
        destinatario.DocumentId = Id;
        _destinatarios.Add(destinatario);
        SetUpdatedAt();
    }

    public void SetXmlContent(string xml, AccessKey accessKey)
    {
        EnsureStatus(DocumentStatus.Draft);
        XmlContent = xml ?? throw new ArgumentNullException(nameof(xml));
        AccessKey = accessKey ?? throw new ArgumentNullException(nameof(accessKey));
        Status = DocumentStatus.XmlGenerated;
        SetUpdatedAt();
    }

    public void SetSignedXml(string signedXml)
    {
        EnsureStatus(DocumentStatus.XmlGenerated);
        SignedXmlContent = signedXml ?? throw new ArgumentNullException(nameof(signedXml));
        Status = DocumentStatus.Signed;
        SetUpdatedAt();
    }

    public void MarkSentToSri()
    {
        EnsureStatus(DocumentStatus.Signed);
        Status = DocumentStatus.SentToSri;
        SetUpdatedAt();
    }

    public void Authorize(string authorizationNumber, DateTime authorizationDate)
    {
        if (Status is not (DocumentStatus.SentToSri or DocumentStatus.PendingRetry))
            throw new DocumentValidationException(
                $"No se puede autorizar el documento en estado {Status}. Debe estar en SentToSri o PendingRetry.");
        SriAuthorizationNumber = authorizationNumber ?? throw new ArgumentNullException(nameof(authorizationNumber));
        SriAuthorizationDate = authorizationDate;
        Status = DocumentStatus.Authorized;
        ProcessedAt = DateTime.UtcNow;
        SetUpdatedAt();

        AddDomainEvent(new DocumentAuthorizedEvent(Id, authorizationNumber, authorizationDate));
    }

    /// <summary>
    /// Marca que el RIDE PDF se generó exitosamente (fija <see cref="RideGeneratedAt"/>). Solo válido
    /// para documentos autorizados — el RIDE es la representación impresa de un comprobante autorizado.
    /// Idempotente: si ya estaba marcado no hace nada.
    /// </summary>
    public void MarkRideGenerated(DateTime? generatedAt = null)
    {
        if (Status is not DocumentStatus.Authorized)
            throw new DocumentValidationException(
                $"No se puede marcar el RIDE como generado en estado {Status}. Debe estar Autorizado.");
        if (RideGeneratedAt is not null)
            return;
        RideGeneratedAt = generatedAt ?? DateTime.UtcNow;
        SetUpdatedAt();
    }

    /// <summary>
    /// Incrementa el contador de intentos de generación del RIDE (usado por
    /// <c>RidePdfRetryService</c> para acotar los reintentos best-effort).
    /// </summary>
    public void IncrementRideRetryCount()
    {
        RideRetryCount++;
        SetUpdatedAt();
    }

    public void Reject(string errorMessage)
    {
        if (Status is not (DocumentStatus.SentToSri or DocumentStatus.PendingRetry))
            throw new DocumentValidationException(
                $"No se puede rechazar el documento en estado {Status}. Debe estar en SentToSri o PendingRetry.");

        ErrorMessage = errorMessage;
        Status = DocumentStatus.Rejected;
        ProcessedAt = DateTime.UtcNow;
        SetUpdatedAt();

        AddDomainEvent(new DocumentRejectedEvent(Id, errorMessage));
    }

    public void ScheduleRetry(DateTime nextRetryAt)
    {
        if (Status is not (DocumentStatus.SentToSri or DocumentStatus.Rejected))
            throw new DocumentValidationException(
                $"No se puede programar un reintento para el documento en estado {Status}.");

        RetryCount++;
        NextRetryAt = nextRetryAt;
        Status = DocumentStatus.PendingRetry;
        SetUpdatedAt();
    }

    public void MarkFailed(string errorMessage)
    {
        ErrorMessage = errorMessage;
        Status = DocumentStatus.Failed;
        ProcessedAt = DateTime.UtcNow;
        SetUpdatedAt();
    }

    public void Void(string reason)
    {
        if (Status is not DocumentStatus.Authorized)
            throw new DocumentValidationException(
                $"No se puede anular el documento en estado {Status}. Debe estar Autorizado.");

        Status = DocumentStatus.Voided;
        ErrorMessage = reason;
        SetUpdatedAt();

        AddDomainEvent(new DocumentVoidedEvent(Id, reason));
    }

    /// <summary>
    /// Claves de PII del comprador que se redactan al anonimizar (PL-2). Se conservan deliberadamente
    /// FUERA de esta lista los campos con relevancia fiscal del comprador exigidos por el SRI para la
    /// retención (p. ej. la identificación del comprador), pero se redactan nombre, dirección, correo y
    /// teléfono que son datos personales sin valor fiscal de archivo.
    /// </summary>
    private static readonly string[] BuyerPiiKeys =
    [
        "razonSocialComprador",
        "direccionComprador",
        "dirComprador",
        "emailComprador",
        "correoComprador",
        "telefonoComprador",
    ];

    /// <summary>
    /// Anonimiza el documento para el borrado con retención fiscal (PL-2): marca el soft-delete y redacta
    /// la PII del comprador, pero RETIENE los campos legalmente obligatorios para la retención fiscal:
    /// claveAcceso, número/fecha de autorización del SRI, el XML firmado, totales/ítems, la identidad de
    /// negocio (estab/ptoEmi/secuencial) y el tipo de documento. La identificación del comprador (RUC/CI)
    /// se conserva por su relevancia fiscal; el resto de la PII (razón social, dirección, email, teléfono)
    /// se reemplaza por un marcador. Idempotente.
    /// </summary>
    public void Anonymize(DateTime? deletedAt = null)
    {
        if (IsAnonymized && DeletedAt is not null)
            return;

        foreach (var key in BuyerPiiKeys)
        {
            if (BuyerInfo.ContainsKey(key))
                BuyerInfo[key] = "[ANONIMIZADO]";
        }

        IsAnonymized = true;
        DeletedAt ??= deletedAt ?? DateTime.UtcNow;
        SetUpdatedAt();
    }

    /// <summary>
    /// Marca el documento como borrado lógicamente sin tocar ningún otro campo (soft-delete puro).
    /// Idempotente. Se usa para documentos no autorizados que igualmente queremos conservar como histórico.
    /// </summary>
    public void SoftDelete(DateTime? deletedAt = null)
    {
        DeletedAt ??= deletedAt ?? DateTime.UtcNow;
        SetUpdatedAt();
    }

    /// <summary>
    /// Transiciones de estado válidas para la máquina de estados del documento.
    /// </summary>
    private static readonly Dictionary<DocumentStatus, DocumentStatus[]> ValidTransitions = new()
    {
        { DocumentStatus.Draft, [DocumentStatus.XmlGenerated] },
        { DocumentStatus.XmlGenerated, [DocumentStatus.Signed] },
        { DocumentStatus.Signed, [DocumentStatus.SentToSri, DocumentStatus.Failed] },
        { DocumentStatus.SentToSri, [DocumentStatus.Authorized, DocumentStatus.Rejected, DocumentStatus.PendingRetry, DocumentStatus.Failed] },
        { DocumentStatus.Rejected, [DocumentStatus.PendingRetry, DocumentStatus.Failed] },
        { DocumentStatus.PendingRetry, [DocumentStatus.Signed, DocumentStatus.Rejected, DocumentStatus.Failed, DocumentStatus.Authorized] },
        { DocumentStatus.Authorized, [DocumentStatus.Voided] },
        { DocumentStatus.Failed, [] },
        { DocumentStatus.Voided, [] }
    };

    public static bool IsValidTransition(DocumentStatus from, DocumentStatus to)
    {
        return ValidTransitions.TryGetValue(from, out var validTargets) && validTargets.Contains(to);
    }

    private void EnsureStatus(DocumentStatus expected)
    {
        if (Status != expected)
            throw new DocumentValidationException(
                $"El documento está en estado {Status}, se esperaba {expected}.");
    }
}
