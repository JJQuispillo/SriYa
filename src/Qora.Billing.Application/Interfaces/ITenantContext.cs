namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Proporciona acceso al contexto del tenant actual para el ámbito de la solicitud.
/// Lo rellena TenantContextMiddleware después de la autenticación.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    void SetTenantId(Guid tenantId);
}
