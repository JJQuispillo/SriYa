namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Implementación con alcance por solicitud de <see cref="ITenantSession"/>.
/// Es un simple contenedor mutable: lo rellena <c>TenantContextMiddleware</c> tras la autenticación.
/// </summary>
public sealed class TenantSession : ITenantSession
{
    public Guid? TenantId { get; private set; }

    public bool IsInTransaction { get; private set; }

    public void SetTenant(Guid tenantId) => TenantId = tenantId;

    public void SetInTransaction(bool inTransaction) => IsInTransaction = inTransaction;
}
