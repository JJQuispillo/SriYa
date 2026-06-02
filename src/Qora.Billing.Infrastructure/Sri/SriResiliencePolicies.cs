using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// Configura los resilience pipelines de Polly 8.x para el HttpClient del SRI.
/// Incluye: retry con exponential backoff, circuit breaker y timeout.
/// </summary>
public static class SriResiliencePolicies
{
    /// <summary>
    /// Registra ISriClient (SriSoapClient) con IHttpClientFactory y un resilience pipeline de Polly.
    /// </summary>
    public static IServiceCollection AddSriClientWithResilience(
        this IServiceCollection services)
    {
        services.AddHttpClient<ISriClient, SriSoapClient>()
            .AddResilienceHandler("sri-resilience", ConfigureResiliencePipeline);

        return services;
    }

    /// <summary>
    /// Configura el resilience pipeline para las solicitudes HTTP al SRI.
    /// El orden importa: Timeout envuelve a CircuitBreaker que envuelve a Retry.
    /// Polly ejecuta las estrategias en el orden de registro (de afuera hacia adentro).
    /// </summary>
    internal static void ConfigureResiliencePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        // Timeout total: 30 segundos por solicitud
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(30),
            Name = "SriRequestTimeout"
        });

        // Retry: 3 reintentos con exponential backoff (2s, 4s, 8s) para errores HTTP transitorios
        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => response.StatusCode >= HttpStatusCode.InternalServerError
                    || response.StatusCode == HttpStatusCode.RequestTimeout),
            Name = "SriRetry",
            OnRetry = static args =>
            {
                // El logging lo maneja la infraestructura del pipeline
                return ValueTask.CompletedTask;
            }
        });

        // Circuit breaker: se abre tras 5 fallos, permanece abierto 30 segundos
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 1.0, // Todas las muestras deben ser fallos
            SamplingDuration = TimeSpan.FromMinutes(1),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => response.StatusCode >= HttpStatusCode.InternalServerError),
            Name = "SriCircuitBreaker"
        });
    }
}
