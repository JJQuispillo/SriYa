namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Scoped implementation of ITenantContext.
/// Holds the current tenant ID for the duration of the HTTP request.
/// </summary>
public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void SetTenantId(Guid tenantId)
    {
        TenantId = tenantId;
    }
}
