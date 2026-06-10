using System.ComponentModel.DataAnnotations;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// Configuración del servicio en segundo plano <c>SriRetryService</c>.
/// ATENCIÓN: estos son reintentos del BACKGROUND SERVICE (un documento pasa por este loop
/// después de fallar la emisión). Son distintos de <see cref="SriConfiguration.MaxRetries"/>,
/// que son los reintentos HTTP de Polly sobre una sola llamada al SRI.
/// </summary>
public class SriRetryConfiguration
{
    public const string SectionName = "Sri:Retry";

    /// <summary>
    /// Intervalo de polling (en segundos) del <c>SriRetryService</c>: cada cuánto revisa si hay
    /// documentos listos para reintentar. Por defecto: 60.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "PollingIntervalSeconds debe estar entre 1 y 3600.")]
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Número máximo de reintentos del BACKGROUND SERVICE antes de marcar un documento como Failed.
    /// Por defecto: 10. DISTINTO de <see cref="SriConfiguration.MaxRetries"/> (reintentos HTTP).
    /// </summary>
    [Range(1, 100, ErrorMessage = "MaxRetries debe estar entre 1 y 100.")]
    public int MaxRetries { get; set; } = 10;

    /// <summary>
    /// Retardo base (en segundos) para el exponential backoff entre reintentos del background.
    /// Por defecto: 300 (5 minutos).
    /// </summary>
    [Range(1, 3600, ErrorMessage = "BaseDelaySeconds debe estar entre 1 y 3600.")]
    public int BaseDelaySeconds { get; set; } = 300;

    /// <summary>
    /// Tope máximo (en segundos) del retardo de exponential backoff. Por defecto: 14400 (4 horas).
    /// </summary>
    [Range(1, 86400, ErrorMessage = "MaxDelaySeconds debe estar entre 1 y 86400.")]
    public int MaxDelaySeconds { get; set; } = 14400;
}
