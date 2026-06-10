// T-CFG-005 spike outcomes (documented for future maintainers):
//  Q4: Polly 8.6.6 confirmed in src/Qora.Billing.Infrastructure/Qora.Billing.Infrastructure.csproj.
//      OnOpened / OnClosed / OnHalfOpened callbacks exist in Polly 8.x CircuitBreakerStrategyOptions<T>.
//  Q5: Polly core 8.6.6's ResilienceContext does NOT expose a ServiceProvider property directly
//      (only a Properties dictionary). The Microsoft.Extensions.Http.Resilience package does set
//      ServiceProvider into ResilienceContext.Properties via internal key, but relying on an
//      undocumented key is fragile. Per the design's Q5 fallback, we capture the IServiceProvider
//      in the closure at registration time and resolve ILoggerFactory from it once. This avoids
//      per-request ServiceProvider resolution and is the most defensive approach.
using System.Net;
using Microsoft.Extensions.Configuration;
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
/// Incluye: timeout, retry con exponential backoff y circuit breaker.
/// Todos los valores del pipeline se leen de <see cref="SriConfiguration"/> (cambia sri-resiliencia-configuracion).
/// </summary>
public static class SriResiliencePolicies
{
    /// <summary>
    /// Registra ISriClient (SriSoapClient) con IHttpClientFactory y un resilience pipeline de Polly.
    /// Toma <see cref="IConfiguration"/> para enlazar la <see cref="SriConfiguration"/> que se usa
    /// en la snapshot local del pipeline (SriSoapClient recibe su propio IOptions por separado en DI).
    /// </summary>
    public static IServiceCollection AddSriClientWithResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Snapshot local para el closure del pipeline. La SriConfiguration que consume
        // SriSoapClient via IOptions se enlaza por separado en DependencyInjection (~línea 122).
        // Ambas lecturas vienen de la misma sección "Sri" y producen POCOs equivalentes.
        var cfg = configuration.GetSection(SriConfiguration.SectionName).Get<SriConfiguration>()
                  ?? new SriConfiguration();

        // Q5 fallback: capturar ILoggerFactory en el closure. BuildServiceProvider aquí es un
        // anti-patrón general, pero aceptable para resolver un ÚNICO singleton (ILoggerFactory)
        // y solo si la registración previa añadió el logging (el host lo hace antes de invocar
        // AddInfrastructureServices). Si no está disponible, los callbacks operan como no-op.
        IServiceProvider? providerSnapshot = null;
        try
        {
            providerSnapshot = services.BuildServiceProvider();
        }
        catch
        {
            // No se pudo construir el provider (e.g. registros posteriores con referencias circulares).
            // Los callbacks operarán como no-op para el logging.
        }
        var loggerFactory = providerSnapshot?.GetService<ILoggerFactory>();
        var pipelineLogger = loggerFactory?.CreateLogger("SriResiliencePolicies");

        services.AddHttpClient<ISriClient, SriSoapClient>()
            .AddResilienceHandler("sri-resilience", b => ConfigureResiliencePipeline(b, cfg, pipelineLogger));

        return services;
    }

    /// <summary>
    /// Configura el pipeline con valores del <paramref name="cfg"/> inyectado.
    /// El orden importa: Timeout envuelve a CircuitBreaker que envuelve a Retry
    /// (Polly ejecuta las estrategias en el orden de registro, de afuera hacia adentro).
    /// Si <see cref="SriConfiguration.ResilienceEnabled"/> es false, el pipeline queda en no-op
    /// (kill switch operacional para rollback sin redeploy durante incidentes con el SRI).
    /// </summary>
    internal static void ConfigureResiliencePipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        SriConfiguration cfg,
        ILogger? pipelineLogger = null)
    {
        if (!cfg.ResilienceEnabled)
        {
            // Pipeline no-op: el operador desactivó la resiliencia vía Sri:ResilienceEnabled=false.
            return;
        }

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds),
            Name = "SriRequestTimeout"
        });

        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = cfg.MaxRetries,
            Delay = TimeSpan.FromSeconds(cfg.BackoffSeconds),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => response.StatusCode >= HttpStatusCode.InternalServerError
                    || response.StatusCode == HttpStatusCode.RequestTimeout),
            Name = "SriRetry",
            OnRetry = args =>
            {
                pipelineLogger?.LogSriRetry("SriRetry", args.AttemptNumber + 1, args.RetryDelay);
                return ValueTask.CompletedTask;
            }
        });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = cfg.CircuitBreakerFailureRatio,
            SamplingDuration = TimeSpan.FromSeconds(cfg.CircuitBreakerSamplingDurationSeconds),
            MinimumThroughput = cfg.CircuitBreakerMinimumThroughput,
            BreakDuration = TimeSpan.FromSeconds(cfg.CircuitBreakerBreakDurationSeconds),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response => response.StatusCode >= HttpStatusCode.InternalServerError),
            Name = "SriCircuitBreaker",
            OnOpened = args =>
            {
                pipelineLogger?.LogSriCircuitOpened("SriCircuitBreaker", args.BreakDuration);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                pipelineLogger?.LogSriCircuitClosed("SriCircuitBreaker");
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = _ =>
            {
                pipelineLogger?.LogSriCircuitHalfOpened("SriCircuitBreaker");
                return ValueTask.CompletedTask;
            }
        });
    }
}
