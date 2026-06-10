namespace Qora.Billing.Domain.Exceptions;

/// <summary>
/// Se lanza cuando el circuit breaker de Polly está abierto (por fallos sostenidos o aislamiento manual)
/// y el HttpClient no puede enviar la solicitud al SRI.
/// Es mapeada por <c>GlobalExceptionHandler</c> a HTTP 503 con un cuerpo
/// <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> que incluye
/// <c>retryAfterSeconds</c> igual a <see cref="BreakDuration"/>.
/// Decisión de diseño D1: vive en Domain para que la capa Application NO tenga que importar Polly.
/// </summary>
public class SriCircuitOpenException : BillingDomainException
{
    /// <summary>
    /// Duración durante la cual el circuito permanecerá abierto. <see cref="TimeSpan.Zero"/>
    /// cuando la causa es aislamiento manual (sin tiempo de break automático).
    /// </summary>
    public TimeSpan BreakDuration { get; }

    /// <summary>
    /// Razón de la apertura del circuito. <c>"fallos sostenidos"</c> cuando es por umbral de fallos;
    /// <c>"circuito aislado manualmente"</c> cuando es por <c>IsolatedCircuitException</c>.
    /// </summary>
    public string Reason { get; }

    public SriCircuitOpenException(TimeSpan breakDuration, Exception innerException)
        : base($"El circuito del SRI está abierto. Reintentar en {breakDuration.TotalSeconds:N0} segundos.", innerException)
    {
        BreakDuration = breakDuration;
        Reason = "fallos sostenidos";
    }

    public SriCircuitOpenException(string reason, Exception innerException)
        : base($"El circuito del SRI está abierto ({reason}).", innerException)
    {
        BreakDuration = TimeSpan.Zero;
        Reason = reason;
    }
}
