using System.ComponentModel.DataAnnotations;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Qora.Billing.Domain.Interfaces;
using Qora.Billing.Infrastructure.Sri;
using Xunit;

namespace Qora.Billing.UnitTests.Infrastructure.Sri;

public class SriResiliencePoliciesTests
{
    [Fact]
    public void BuildsPipeline_WithDefaultConfig_RegistersCircuitBreakerWithExpectedOptions()
    {
        // T-CFG-018 — verificar que los valores de la config se aplican al
        // CircuitBreakerStrategyOptions. Polly 8 no expone descriptor público, así que
        // capturamos el valor del campo interno del builder via reflection.
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        var cfg = new SriConfiguration();

        SriResiliencePolicies.ConfigureResiliencePipeline(builder, cfg);

        var cbOptions = TryGetCircuitBreakerOptions(builder);
        Assert.NotNull(cbOptions);
        Assert.Equal(cfg.CircuitBreakerFailureRatio, cbOptions!.FailureRatio);
        Assert.Equal(TimeSpan.FromSeconds(cfg.CircuitBreakerSamplingDurationSeconds), cbOptions.SamplingDuration);
        Assert.Equal(cfg.CircuitBreakerMinimumThroughput, cbOptions.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(cfg.CircuitBreakerBreakDurationSeconds), cbOptions.BreakDuration);
        Assert.Equal("SriCircuitBreaker", cbOptions.Name);
    }

    [Fact]
    public void BuildsPipeline_WithResilienceEnabledFalse_RegistersNoStrategies()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        var cfg = new SriConfiguration { ResilienceEnabled = false };

        SriResiliencePolicies.ConfigureResiliencePipeline(builder, cfg);

        var cbOptions = TryGetCircuitBreakerOptions(builder);
        Assert.Null(cbOptions);
    }

    [Fact]
    public void BuildsPipeline_WithCustomConfig_AppliesCircuitBreakerValues()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        var cfg = new SriConfiguration
        {
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerSamplingDurationSeconds = 30,
            CircuitBreakerMinimumThroughput = 2,
            CircuitBreakerBreakDurationSeconds = 10
        };

        SriResiliencePolicies.ConfigureResiliencePipeline(builder, cfg);

        var cbOptions = TryGetCircuitBreakerOptions(builder);
        Assert.NotNull(cbOptions);
        Assert.Equal(0.5, cbOptions!.FailureRatio);
        Assert.Equal(TimeSpan.FromSeconds(30), cbOptions.SamplingDuration);
        Assert.Equal(2, cbOptions.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(10), cbOptions.BreakDuration);
    }

    [Fact]
    public void BuildsPipeline_WithDefaultConfig_RegistersTimeoutWithExpectedValue()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        var cfg = new SriConfiguration();

        SriResiliencePolicies.ConfigureResiliencePipeline(builder, cfg);

        var timeout = TryGetTimeoutOptions(builder);
        Assert.NotNull(timeout);
        Assert.Equal(TimeSpan.FromSeconds(cfg.TimeoutSeconds), timeout!.Timeout);
        Assert.Equal("SriRequestTimeout", timeout.Name);
    }

    [Fact]
    public void BuildsPipeline_WithDefaultConfig_RegistersRetryWithExpectedValue()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        var cfg = new SriConfiguration();

        SriResiliencePolicies.ConfigureResiliencePipeline(builder, cfg);

        var retry = TryGetRetryOptions(builder);
        Assert.NotNull(retry);
        Assert.Equal(cfg.MaxRetries, retry!.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(cfg.BackoffSeconds), retry.Delay);
        Assert.Equal(DelayBackoffType.Exponential, retry.BackoffType);
        Assert.Equal("SriRetry", retry.Name);
    }

    [Fact]
    public void AddSriClientWithResilience_RegistersNamedClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sri:TimeoutSeconds"] = "30",
                ["Sri:MaxRetries"] = "3"
            })
            .Build();

        services.AddSriClientWithResilience(config);
        var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IHttpClientFactory>();
        // AddHttpClient<ISriClient, SriSoapClient>() sin name → nombre = full type name del interface.
        var client = factory.CreateClient(typeof(ISriClient).FullName!);

        Assert.NotNull(client);
    }

    [Fact]
    public void SriConfiguration_DefaultsMatchBaseline()
    {
        // T-CFG-011 / S-CFG-005: defaults idénticos a los valores hardcoded previos.
        var cfg = new SriConfiguration();
        cfg.TimeoutSeconds.Should().Be(30);
        cfg.MaxRetries.Should().Be(3);
        cfg.BackoffSeconds.Should().Be(2);
        cfg.CircuitBreakerFailureRatio.Should().Be(1.0);
        cfg.CircuitBreakerSamplingDurationSeconds.Should().Be(60);
        cfg.CircuitBreakerBreakDurationSeconds.Should().Be(30);
        cfg.CircuitBreakerMinimumThroughput.Should().Be(5);
        cfg.ResilienceEnabled.Should().BeTrue();
    }

    [Fact]
    public void SriConfiguration_ValidatesWithRangeAttributes()
    {
        var cfg = new SriConfiguration { BackoffSeconds = -1 };
        var ctx = new ValidationContext(cfg);
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(cfg, ctx, results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(SriConfiguration.BackoffSeconds)));
    }

    [Fact]
    public void SriRetryConfiguration_DefaultsMatchBaseline()
    {
        var cfg = new SriRetryConfiguration();
        cfg.PollingIntervalSeconds.Should().Be(60);
        cfg.MaxRetries.Should().Be(10);
        cfg.BaseDelaySeconds.Should().Be(300);
        cfg.MaxDelaySeconds.Should().Be(14400);
    }

    // ────────── Helpers ──────────

    /// <summary>
    /// Polly 8 no expone descriptor público en ResiliencePipelineBuilder. Reflection sobre
    /// el campo interno 'components' o equivalente para extraer la primera
    /// CircuitBreakerStrategyOptions registrada.
    /// </summary>
    private static CircuitBreakerStrategyOptions<HttpResponseMessage>? TryGetCircuitBreakerOptions(
        ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        var components = GetComponentsViaReflection(builder);
        foreach (var comp in components)
        {
            var optionsProp = comp.GetType().GetProperty("Options",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (optionsProp?.GetValue(comp) is CircuitBreakerStrategyOptions<HttpResponseMessage> cb)
                return cb;
        }
        return null;
    }

    private static Polly.Timeout.TimeoutStrategyOptions? TryGetTimeoutOptions(
        ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        var components = GetComponentsViaReflection(builder);
        foreach (var comp in components)
        {
            var optionsProp = comp.GetType().GetProperty("Options",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (optionsProp?.GetValue(comp) is Polly.Timeout.TimeoutStrategyOptions to)
                return to;
        }
        return null;
    }

    private static Polly.Retry.RetryStrategyOptions<HttpResponseMessage>? TryGetRetryOptions(
        ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        var components = GetComponentsViaReflection(builder);
        foreach (var comp in components)
        {
            var optionsProp = comp.GetType().GetProperty("Options",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (optionsProp?.GetValue(comp) is Polly.Retry.RetryStrategyOptions<HttpResponseMessage> ro)
                return ro;
        }
        return null;
    }

    private static List<object> GetComponentsViaReflection(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        // Polly 8.6.6 ResiliencePipelineBuilderBase tiene un campo privado '_entries' de tipo
        // List<ResilienceStrategyDescriptor> en el que se van acumulando las strategies.
        var type = builder.GetType();
        var baseType = type.BaseType; // ResiliencePipelineBuilderBase
        var field = baseType?.GetField("_entries",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(builder) is System.Collections.IEnumerable enumerable)
        {
            var list = new List<object>();
            foreach (var item in enumerable) list.Add(item);
            return list;
        }
        return new List<object>();
    }
}
