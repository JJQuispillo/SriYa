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
    public DocumentStatus Status { get; private set; }
    public string? XmlContent { get; private set; }
    public string? SignedXmlContent { get; private set; }
    public string? SriAuthorizationNumber { get; private set; }
    public DateTime? SriAuthorizationDate { get; private set; }

    /// <summary>
    /// Issuer information stored as JSON-ready dictionary for flexibility across document types.
    /// </summary>
    public Dictionary<string, string> IssuerInfo { get; private set; } = new();

    /// <summary>
    /// Buyer/receiver information stored as JSON-ready dictionary.
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

        document.AddDomainEvent(new DocumentCreatedEvent(document.Id, tenantId, documentType));
        return document;
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
                $"Cannot authorize document in status {Status}. Must be SentToSri or PendingRetry.");
        SriAuthorizationNumber = authorizationNumber ?? throw new ArgumentNullException(nameof(authorizationNumber));
        SriAuthorizationDate = authorizationDate;
        Status = DocumentStatus.Authorized;
        ProcessedAt = DateTime.UtcNow;
        SetUpdatedAt();

        AddDomainEvent(new DocumentAuthorizedEvent(Id, authorizationNumber, authorizationDate));
    }

    public void Reject(string errorMessage)
    {
        if (Status is not (DocumentStatus.SentToSri or DocumentStatus.PendingRetry))
            throw new DocumentValidationException(
                $"Cannot reject document in status {Status}. Must be SentToSri or PendingRetry.");

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
                $"Cannot schedule retry for document in status {Status}.");

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
                $"Cannot void document in status {Status}. Must be Authorized.");

        Status = DocumentStatus.Voided;
        ErrorMessage = reason;
        SetUpdatedAt();

        AddDomainEvent(new DocumentVoidedEvent(Id, reason));
    }

    /// <summary>
    /// Valid status transitions for the document state machine.
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
                $"Document is in status {Status}, expected {expected}.");
    }
}
