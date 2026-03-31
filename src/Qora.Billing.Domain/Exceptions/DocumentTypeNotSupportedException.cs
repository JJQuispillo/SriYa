using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Thrown when a document type has no registered strategy.
/// </summary>
public class DocumentTypeNotSupportedException : BillingDomainException
{
    public DocumentType DocumentType { get; }

    public DocumentTypeNotSupportedException(DocumentType documentType)
        : base($"Document type '{documentType}' is not supported or has no registered strategy.")
    {
        DocumentType = documentType;
    }
}
