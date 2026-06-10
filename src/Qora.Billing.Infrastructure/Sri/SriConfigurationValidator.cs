using Microsoft.Extensions.Options;

namespace Qora.Billing.Infrastructure.Sri;

/// <summary>
/// Validador de <see cref="SriConfiguration"/> para reglas cross-property
/// que <see cref="System.ComponentModel.DataAnnotations"/> no puede expresar.
/// Se registra vía <c>services.AddSingleton&lt;IValidateOptions&lt;SriConfiguration&gt;, SriConfigurationValidator&gt;()</c>
/// y se ejecuta automáticamente al construir el <c>IServiceProvider</c> (fail-fast en startup).
/// </summary>
public class SriConfigurationValidator : IValidateOptions<SriConfiguration>
{
    public ValidateOptionsResult Validate(string? name, SriConfiguration options)
    {
        var errors = new List<string>();

        if (options.CircuitBreakerBreakDurationSeconds >= options.CircuitBreakerSamplingDurationSeconds)
        {
            errors.Add(
                $"Sri:CircuitBreaker:BreakDurationSeconds ({options.CircuitBreakerBreakDurationSeconds}) " +
                $"debe ser MENOR que SamplingDurationSeconds ({options.CircuitBreakerSamplingDurationSeconds}). " +
                "Polly no puede muestrear suficientes eventos para cerrar el circuito si el break es más largo que el sampling.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
