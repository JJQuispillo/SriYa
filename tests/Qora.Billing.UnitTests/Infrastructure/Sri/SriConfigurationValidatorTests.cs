using FluentAssertions;
using Microsoft.Extensions.Options;
using Qora.Billing.Infrastructure.Sri;

namespace Qora.Billing.UnitTests.Infrastructure.Sri;

public class SriConfigurationValidatorTests
{
    [Fact]
    public void Validate_WhenBreakDurationExceedsSampling_ReturnsFail()
    {
        var validator = new SriConfigurationValidator();
        var cfg = new SriConfiguration
        {
            CircuitBreakerBreakDurationSeconds = 60,
            CircuitBreakerSamplingDurationSeconds = 30
        };

        var result = validator.Validate("Sri", cfg);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f =>
            f.Contains("BreakDurationSeconds") &&
            f.Contains("SamplingDurationSeconds") &&
            f.Contains("60") &&
            f.Contains("30"));
    }

    [Fact]
    public void Validate_WhenValid_ReturnsSuccess()
    {
        var validator = new SriConfigurationValidator();
        var cfg = new SriConfiguration
        {
            CircuitBreakerBreakDurationSeconds = 30,
            CircuitBreakerSamplingDurationSeconds = 60
        };

        var result = validator.Validate("Sri", cfg);

        result.Failed.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenEqual_ReturnsFail()
    {
        // Polly no puede muestrear eventos suficientes si BreakDuration == SamplingDuration
        // (no hay ventana de evaluación abierta).
        var validator = new SriConfigurationValidator();
        var cfg = new SriConfiguration
        {
            CircuitBreakerBreakDurationSeconds = 30,
            CircuitBreakerSamplingDurationSeconds = 30
        };

        var result = validator.Validate("Sri", cfg);

        result.Failed.Should().BeTrue();
    }
}
