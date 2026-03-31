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
/// Configures Polly 8.x resilience pipelines for the SRI HttpClient.
/// Includes: retry with exponential backoff, circuit breaker, and timeout.
/// </summary>
public static class SriResiliencePolicies
{
    /// <summary>
    /// Registers ISriClient (SriSoapClient) with IHttpClientFactory and a Polly resilience pipeline.
    /// </summary>
    public static IServiceCollection AddSriClientWithResilience(
        this IServiceCollection services)
    {
        services.AddHttpClient<ISriClient, SriSoapClient>()
            .AddResilienceHandler("sri-resilience", ConfigureResiliencePipeline);

        return services;
    }

    /// <summary>
    /// Configures the resilience pipeline for SRI HTTP requests.
    /// Order matters: Timeout wraps CircuitBreaker wraps Retry.
    /// Polly executes strategies in registration order (outer to inner).
    /// </summary>
    internal static void ConfigureResiliencePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        // Total timeout: 30 seconds per request
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(30),
            Name = "SriRequestTimeout"
        });

        // Retry: 3 retries with exponential backoff (2s, 4s, 8s) for transient HTTP errors
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
                // Logging is handled by the pipeline infrastructure
                return ValueTask.CompletedTask;
            }
        });

        // Circuit breaker: open after 5 failures, stay open for 30 seconds
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 1.0, // All sampled must be failures
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
