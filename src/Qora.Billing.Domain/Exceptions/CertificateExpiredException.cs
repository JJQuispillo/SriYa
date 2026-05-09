namespace Qora.Billing.Domain.Exceptions;

public class CertificateExpiredException : BillingDomainException
{
    public Guid TenantId { get; }
    public DateTime ExpirationDate { get; }

    public CertificateExpiredException(Guid tenantId, DateTime expirationDate)
        : base($"La firma electrónica del tenant {tenantId} venció el {expirationDate:yyyy-MM-dd}.")
    {
        TenantId = tenantId;
        ExpirationDate = expirationDate;
    }
}
