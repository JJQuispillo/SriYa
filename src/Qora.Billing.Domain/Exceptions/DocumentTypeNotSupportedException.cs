using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Thrown when a document type has no registered strategy.
/// </summary>
public class DocumentTypeNotSupportedException : BillingDomainException
{
    public DocumentType DocumentType { get; }

    public DocumentTypeNotSupportedException(DocumentType documentType)
        : base($"El tipo de documento '{documentType}' no está soportado o no tiene una estrategia registrada.")
    {
        DocumentType = documentType;
    }
}
