namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Sesión de tenant con alcance por solicitud usada por la capa de Infrastructure para fijar
/// la GUC (Grand Unified Configuration variable) <c>app.current_tenant</c> de Postgres de forma
/// transaction-local mediante el <c>TenantCommandInterceptor</c>.
///
/// Es distinta de <see cref="ITenantContext"/>: ITenantContext expone el tenant a la capa de
/// aplicación, mientras que ITenantSession lleva además la bandera de "dentro de transacción"
/// que el interceptor necesita para decidir si puede emitir un <c>set_config(..., true)</c>
/// (que es transaction-local y por tanto requiere una transacción abierta).
/// </summary>
public interface ITenantSession
{
    /// <summary>Tenant actual, o null cuando la solicitud corre en un camino cross-tenant (privilegiado).</summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Indica que la solicitud abrió una transacción ambiental sobre la que se puede enlazar
    /// el <c>set_config('app.current_tenant', ..., true)</c> transaction-local.
    /// </summary>
    bool IsInTransaction { get; }

    /// <summary>Establece el tenant actual para el alcance de la solicitud.</summary>
    void SetTenant(Guid tenantId);

    /// <summary>Marca la sesión como dentro de una transacción ambiental (o fuera de ella).</summary>
    void SetInTransaction(bool inTransaction);
}
