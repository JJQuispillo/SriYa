namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Registro de idempotencia por (TenantId, Key). Permite el replay de una emisión: ante un reintento
/// con la misma clave y el mismo cuerpo, se devuelve la respuesta original SIN volver a emitir al SRI.
///
/// NO se apoya en claveAcceso (su numericCode es aleatorio e inestable entre reintentos). La identidad
/// del replay es la clave provista por el cliente más el hash determinista del request.
///
/// Está acotada por tenant (columna desnormalizada tenant_id) y por tanto cubierta por RLS, igual que el
/// resto de tablas con alcance de tenant. La inserción inicial (status=in_progress) actúa como lock:
/// el unique (tenant_id, idempotency_key) hace que dos peticiones concurrentes con la misma clave colisionen.
/// </summary>
public class IdempotencyKey : BaseEntity
{
    /// <summary>Estado: la emisión está en curso (insert-as-lock); aún sin respuesta almacenada.</summary>
    public const string StatusInProgress = "in_progress";

    /// <summary>Estado: la emisión terminó y <see cref="ResponseSnapshot"/> contiene la respuesta a reproducir.</summary>
    public const string StatusCompleted = "completed";

    public Guid TenantId { get; private set; }

    /// <summary>Clave de idempotencia provista por el cliente (header Idempotency-Key).</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>Hash determinista del cuerpo del request, para detectar reuso de clave con payload distinto.</summary>
    public string RequestHash { get; private set; } = string.Empty;

    public string Status { get; private set; } = StatusInProgress;

    /// <summary>Snapshot JSON de la <c>DocumentResponse</c> original; null mientras está in_progress.</summary>
    public string? ResponseSnapshot { get; private set; }

    /// <summary>Id del documento emitido (cuando completa). Útil para auditoría/limpieza.</summary>
    public Guid? DocumentId { get; private set; }

    /// <summary>Fecha de expiración (TTL/retención). Una barrida en segundo plano elimina las vencidas.</summary>
    public DateTime ExpiresAt { get; private set; }

    private IdempotencyKey() { } // EF Core

    /// <summary>
    /// Crea el registro de lock inicial (status=in_progress) para una emisión que comienza.
    /// </summary>
    public static IdempotencyKey Start(Guid tenantId, string key, string requestHash, DateTime expiresAt)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("La clave de idempotencia no puede estar vacía.", nameof(key));
        if (string.IsNullOrWhiteSpace(requestHash))
            throw new ArgumentException("El hash del request no puede estar vacío.", nameof(requestHash));

        return new IdempotencyKey
        {
            TenantId = tenantId,
            Key = key,
            RequestHash = requestHash,
            Status = StatusInProgress,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// Marca el registro como completado y almacena el snapshot de la respuesta a reproducir.
    /// </summary>
    public void Complete(string responseSnapshot, Guid documentId)
    {
        ResponseSnapshot = responseSnapshot ?? throw new ArgumentNullException(nameof(responseSnapshot));
        DocumentId = documentId;
        Status = StatusCompleted;
        SetUpdatedAt();
    }

    public bool IsCompleted => Status == StatusCompleted;
}
