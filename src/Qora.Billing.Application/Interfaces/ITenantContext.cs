namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Provides access to the current tenant context for the request scope.
/// Populated by TenantContextMiddleware after authentication.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    void SetTenantId(Guid tenantId);
}
