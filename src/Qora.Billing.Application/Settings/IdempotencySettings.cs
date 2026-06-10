namespace Qora.Billing.Application.Settings;

/// <summary>
/// Configuración de idempotencia (replay por Idempotency-Key).
/// Vinculada a la sección "Idempotency" en appsettings.
/// </summary>
public class IdempotencySettings
{
    public const string SectionName = "Idempotency";

    /// <summary>
    /// Días de retención de los registros de idempotencia. Define el TTL (expires_at = created_at +
    /// RetentionDays). Una barrida en segundo plano elimina los vencidos. Por defecto 7 días.
    /// </summary>
    public int RetentionDays { get; set; } = 7;
}
