using System.ComponentModel.DataAnnotations;

namespace Qora.Billing.Application.Settings;

/// <summary>
/// Opciones del servicio en segundo plano <c>RidePdfRetryService</c>, la red de seguridad que regenera
/// el RIDE PDF de los documentos autorizados cuya generación best-effort en la emisión falló (el
/// <c>catch</c> que traga la excepción en <c>ProcessDocumentCommandHandler</c>).
/// Vinculada a la sección "Sri:RidePdfRetry" en appsettings.
/// </summary>
/// <remarks>
/// El RIDE no se persiste (se genera on-demand). El marcador de éxito es la columna
/// <c>ride_generated_at</c>; un documento autorizado con <c>ride_generated_at IS NULL</c> es candidato a
/// regeneración. Los defaults usan SEGUNDOS por consistencia con <see cref="SriReconciliationOptions"/>.
/// </remarks>
public class RidePdfRetryOptions
{
    public const string SectionName = "Sri:RidePdfRetry";

    /// <summary>
    /// Segundos entre barridos. Para DESACTIVAR efectivamente el servicio (escape hatch operacional),
    /// poner esto en <c>86400</c> (24h) o mayor.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Un documento autorizado se considera candidato cuando su <c>ProcessedAt</c> (momento de
    /// autorización) es anterior a <c>now - StaleAfterSeconds</c>. Esto da margen a la generación
    /// inline best-effort de la emisión antes de que el barrido intervenga. Default 120s.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int StaleAfterSeconds { get; set; } = 120;

    /// <summary>
    /// Máximo de intentos de regeneración por documento antes de dejar de reintentar (se loguea y se
    /// requiere atención manual). Acota el reintento vía la columna <c>ride_retry_count</c>. Default 5.
    /// </summary>
    [Range(1, 100)]
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Máximo de documentos procesados por barrido. Acota la ventana de retención del lock
    /// <c>FOR UPDATE SKIP LOCKED</c>. Default 50.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxBatchSize { get; set; } = 50;
}
