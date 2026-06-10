using System.ComponentModel.DataAnnotations;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// POCO de configuración para los endpoints y timeouts de los servicios web SOAP del SRI.
/// Se enlaza desde la sección "Sri" de appsettings.json.
/// </summary>
public class SriConfiguration
{
    public const string SectionName = "Sri";

    /// <summary>
    /// Ambiente del SRI: Test (1) o Production (2). Por defecto: Test.
    /// </summary>
    public EnvironmentType Environment { get; set; } = EnvironmentType.Test;

    /// <summary>
    /// URL del servicio de Recepcion (validarComprobante).
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline?wsdl
    /// </summary>
    public string RecepcionUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL del servicio de Autorizacion (autorizacionComprobante).
    /// Test: https://celcer.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// Prod: https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline?wsdl
    /// </summary>
    public string AutorizacionUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL de producción del servicio de Recepcion (validarComprobante).
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/RecepcionComprobantesOffline
    /// </summary>
    public string RecepcionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// URL de producción del servicio de Autorizacion (autorizacionComprobante).
    /// https://cel.sri.gob.ec/comprobantes-electronicos-ws/AutorizacionComprobantesOffline
    /// </summary>
    public string AutorizacionUrlProduccion { get; set; } = string.Empty;

    /// <summary>
    /// Timeout de las solicitudes HTTP en segundos. Por defecto: 30.
    /// Alimenta el <see cref="Polly.Timeout.TimeoutStrategyOptions.Timeout"/> del pipeline Polly.
    /// </summary>
    [Range(1, 600, ErrorMessage = "TimeoutSeconds debe estar entre 1 y 600.")]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Número máximo de intentos de reintento HTTP para errores transitorios del SRI. Por defecto: 3.
    /// ATENCIÓN: estos son los reintentos HTTP de Polly sobre una sola llamada al SRI.
    /// Son DISTINTOS de <see cref="SriRetryConfiguration.MaxRetries"/>, que son los reintentos del
    /// background service <c>SriRetryService</c> sobre un documento.
    /// Alimenta el <see cref="Polly.Retry.RetryStrategyOptions{TResult}.MaxRetryAttempts"/>.
    /// </summary>
    [Range(0, 20, ErrorMessage = "MaxRetries debe estar entre 0 y 20.")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retardo base (en segundos) para el exponential backoff entre reintentos HTTP. Por defecto: 2.
    /// Alimenta el <see cref="Polly.Retry.RetryStrategyOptions{TResult}.Delay"/>.
    /// </summary>
    [Range(0, 300, ErrorMessage = "BackoffSeconds debe estar entre 0 y 300.")]
    public int BackoffSeconds { get; set; } = 2;

    /// <summary>
    /// Ratio de fallos (0.0 a 1.0) requerido dentro de la ventana de muestreo para que el circuit breaker
    /// se abra. Por defecto: 1.0 (todas las muestras deben ser fallos).
    /// Alimenta el <see cref="Polly.CircuitBreaker.CircuitBreakerStrategyOptions{TResult}.FailureRatio"/>.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "CircuitBreakerFailureRatio debe estar entre 0.0 y 1.0.")]
    public double CircuitBreakerFailureRatio { get; set; } = 1.0;

    /// <summary>
    /// Duración (en segundos) de la ventana de muestreo en la que el circuit breaker contabiliza los fallos.
    /// Por defecto: 60.
    /// Alimenta el <see cref="Polly.CircuitBreaker.CircuitBreakerStrategyOptions{TResult}.SamplingDuration"/>.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "CircuitBreakerSamplingDurationSeconds debe estar entre 1 y 3600.")]
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 60;

    /// <summary>
    /// Duración (en segundos) que el circuito permanece abierto antes de pasar a half-open. Por defecto: 30.
    /// Alimenta el <see cref="Polly.CircuitBreaker.CircuitBreakerStrategyOptions{TResult}.BreakDuration"/>.
    /// </summary>
    [Range(1, 600, ErrorMessage = "CircuitBreakerBreakDurationSeconds debe estar entre 1 y 600.")]
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Número mínimo de eventos (éxitos + fallos) que deben ocurrir dentro de la ventana de muestreo
    /// para que el circuit breaker evalúe abrirse. Por defecto: 5.
    /// Alimenta el <see cref="Polly.CircuitBreaker.CircuitBreakerStrategyOptions{TResult}.MinimumThroughput"/>.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "CircuitBreakerMinimumThroughput debe estar entre 1 y 1000.")]
    public int CircuitBreakerMinimumThroughput { get; set; } = 5;

    /// <summary>
    /// Kill switch operacional. Cuando es <c>false</c>, el pipeline de Polly se registra en modo
    /// no-op (sin timeout, sin retry, sin circuit breaker). Útil para rollback inmediato sin redeploy
    /// durante incidentes con el SRI. Por defecto: <c>true</c>.
    /// </summary>
    public bool ResilienceEnabled { get; set; } = true;
}
