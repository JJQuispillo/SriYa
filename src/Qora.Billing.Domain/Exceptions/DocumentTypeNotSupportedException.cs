using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Se lanza cuando un tipo de documento no tiene una estrategia registrada.
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
