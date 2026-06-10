namespace Qora.Billing.Application.Interfaces;

/// <summary>
/// Resultado de un borrado con alcance por emisor (PL-2). Reporta qué se hizo, de forma auditable.
/// </summary>
public sealed record ScopedDeleteResult(
    Guid TenantId,
    int AuthorizedAnonymized,
    int AuthorizedHardDeleted,
    int NonAuthorizedHardDeleted,
    bool TenantAnonymized,
    bool TenantHardDeleted,
    long ExportSizeBytes);

/// <summary>
/// Servicio de ciclo de vida por emisor (tenant). TODAS las operaciones son ACOTADAS al tenant indicado
/// (RLS-safe): se ejecutan bajo el contexto billing_app con la GUC app.current_tenant ya fijada por el
/// middleware, de modo que sólo ven/afectan datos de ese emisor. NO son operaciones all-tenant.
/// </summary>
public interface ITenantLifecycleService
{
    /// <summary>
    /// PL-1: exporta TODOS los datos del emisor (perfil del tenant, certificados —metadata, sin claves
    /// privadas descifradas—, documentos + XML/RIDE + ítems/destinatarios + eventos + metadata de api-keys
    /// + claves de idempotencia) como un ZIP, escrito en <paramref name="output"/> en streaming.
    /// Devuelve el número de bytes escritos. Acotado al tenant en contexto.
    /// </summary>
    Task<long> ExportTenantAsync(Guid tenantId, Stream output, CancellationToken cancellationToken = default);

    /// <summary>
    /// PL-2: exporta primero (siempre) y luego borra los datos del emisor según la política de retención:
    /// los no autorizados se eliminan físicamente; los autorizados se anonimizan por defecto, o se eliminan
    /// físicamente sólo si <c>Lifecycle:AllowHardDeleteAuthorized=true</c>. Respeta el orden FK RESTRICT
    /// (hijos/documentos antes que el tenant). Idempotente. Acotado al tenant en contexto.
    /// </summary>
    Task<ScopedDeleteResult> DeleteTenantDataAsync(
        Guid tenantId, Stream exportOutput, CancellationToken cancellationToken = default);
}
