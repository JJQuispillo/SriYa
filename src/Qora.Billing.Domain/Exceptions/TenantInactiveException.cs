namespace Qora.Billing.Domain.Exceptions;

public class TenantInactiveException : BillingDomainException
{
    public Guid TenantId { get; }

    public TenantInactiveException(Guid tenantId)
        : base($"Tenant {tenantId} is inactive.")
    {
        TenantId = tenantId;
    }
}
