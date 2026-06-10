namespace Qora.Billing.Domain.Exceptions;

public class TenantInactiveException : BillingDomainException
{
    public Guid TenantId { get; }

    public TenantInactiveException(Guid tenantId)
        : base($"El tenant {tenantId} se encuentra inactivo.")
    {
        TenantId = tenantId;
    }
}
