namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Implementación scoped de ITenantContext.
/// Mantiene el ID del tenant actual durante toda la solicitud HTTP.
/// </summary>
public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void SetTenantId(Guid tenantId)
    {
        TenantId = tenantId;
    }
}
