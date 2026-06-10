namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Se lanza cuando una emisión choca con el unique constraint de identidad de negocio
/// (ux_documents_business_identity: tenant_id, document_type, estab, pto_emision, secuencial).
/// El handler la captura y deduplica devolviendo el comprobante existente (nunca un 500), de modo que
/// un reintento sin Idempotency-Key tampoco genera un segundo comprobante (II-2).
/// </summary>
public class DuplicateBusinessIdentityException : BillingDomainException
{
    public DuplicateBusinessIdentityException(string message) : base(message)
    {
    }

    public DuplicateBusinessIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
