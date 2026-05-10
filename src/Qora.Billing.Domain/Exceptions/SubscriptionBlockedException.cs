namespace Qora.Billing.Domain.Exceptions;

public class SubscriptionBlockedException : BillingDomainException
{
    public Guid TenantId { get; }

    public SubscriptionBlockedException(Guid tenantId)
        : base($"La suscripción del tenant {tenantId} está suspendida o cancelada.")
    {
        TenantId = tenantId;
    }
}
