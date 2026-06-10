using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Domain.Interfaces;

/// <summary>
/// Persistencia de los registros de idempotencia (tabla idempotency_keys), acotada por tenant y por
/// tanto sujeta a RLS: todas las operaciones corren bajo el rol normal de la app (billing_app) con la
/// GUC app.current_tenant fijada por la transacción ambiental del request.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Busca un registro existente por (tenant actual, key). Devuelve null si no existe.
    /// </summary>
    Task<IdempotencyKey?> FindAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserta el registro de lock inicial (status=in_progress) y persiste de inmediato. El unique
    /// (tenant_id, idempotency_key) hace que una segunda petición concurrente con la misma clave falle
    /// con violación de unicidad: el llamador debe tratarla como "ya en curso / ya emitido".
    /// </summary>
    /// <returns>true si se insertó; false si ya existía (colisión de unicidad).</returns>
    Task<bool> TryStartAsync(IdempotencyKey entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca el registro como completado con el snapshot de la respuesta y persiste.
    /// </summary>
    Task CompleteAsync(IdempotencyKey entry, CancellationToken cancellationToken = default);
}
