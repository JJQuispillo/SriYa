using Microsoft.Extensions.Logging;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// Constantes de <see cref="EventId"/> y helpers de logging para los eventos
/// del resilience pipeline del SRI.
/// Convención de IDs:
/// 1001-1009 → transiciones del circuit breaker (Polly CircuitBreakerStrategy);
/// 1010-1019 → reintentos (Polly RetryStrategy);
/// 1020+ → reservado para uso futuro.
/// </summary>
public static class SriCircuitEvents
{
    public static readonly EventId CircuitOpened = new(1001, "SriCircuitOpened");
    public static readonly EventId CircuitClosed = new(1002, "SriCircuitClosed");
    public static readonly EventId CircuitHalfOpened = new(1003, "SriCircuitHalfOpened");
    public static readonly EventId Retry = new(1010, "SriRetry");

    /// <summary>
    /// Emite un log estructurado con <see cref="EventId"/> 1001 cuando el circuit breaker
    /// se abre por una racha sostenida de fallos.
    /// </summary>
    public static void LogSriCircuitOpened(this ILogger logger, string circuitName, TimeSpan breakDuration)
        => logger.LogWarning(CircuitOpened,
            "SRI circuit breaker OPENED: {CircuitName} will not accept requests for {BreakDurationSeconds}s",
            circuitName, (int)breakDuration.TotalSeconds);

    /// <summary>
    /// Emite un log estructurado con <see cref="EventId"/> 1002 cuando el circuit breaker se cierra.
    /// </summary>
    public static void LogSriCircuitClosed(this ILogger logger, string circuitName)
        => logger.LogInformation(CircuitClosed,
            "SRI circuit breaker CLOSED: {CircuitName} accepting requests again", circuitName);

    /// <summary>
    /// Emite un log estructurado con <see cref="EventId"/> 1003 cuando el circuit breaker
    /// pasa a half-open para probar la siguiente request.
    /// </summary>
    public static void LogSriCircuitHalfOpened(this ILogger logger, string circuitName)
        => logger.LogInformation(CircuitHalfOpened,
            "SRI circuit breaker HALF-OPEN: {CircuitName} probing the next request", circuitName);

    /// <summary>
    /// Emite un log estructurado con <see cref="EventId"/> 1010 cuando se va a ejecutar un reintento.
    /// </summary>
    public static void LogSriRetry(this ILogger logger, string strategyName, int attempt, TimeSpan delay)
        => logger.LogInformation(Retry,
            "SRI retry attempt {Attempt} via {StrategyName}, next delay {DelaySeconds}s",
            attempt, strategyName, (int)delay.TotalSeconds);
}
