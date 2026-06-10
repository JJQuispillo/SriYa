using Microsoft.Extensions.Options;
using Qora.Billing.Application.Settings;

namespace Qora.Billing.Infrastructure.Caching;

/// <summary>
/// Validador de <see cref="CacheOptions"/> para reglas cross-property que
/// <see cref="System.ComponentModel.DataAnnotations"/> no puede expresar.
/// Se registra vía <c>services.AddSingleton&lt;IValidateOptions&lt;CacheOptions&gt;, CacheOptionsValidator&gt;()</c>
/// y se ejecuta al construir el <c>IServiceProvider</c> (fail-fast en startup), espejando
/// <c>SriConfigurationValidator</c>. Un Redis mal configurado DEBE abortar el arranque, no fallar
/// de forma diferida en el primer uso del cache.
/// </summary>
public class CacheOptionsValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        var errors = new List<string>();

        if (options.Provider == CacheProvider.Redis && string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            errors.Add(
                "Cache:RedisConnectionString es obligatorio (no vacío) cuando Cache:Provider = Redis. " +
                "Un Redis mal configurado debe abortar el arranque (fail-fast), no fallar en el primer uso del cache.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
