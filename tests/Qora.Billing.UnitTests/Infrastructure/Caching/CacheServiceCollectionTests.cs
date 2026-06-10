using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Settings;
using Qora.Billing.Infrastructure;
using Qora.Billing.Infrastructure.Caching;

namespace Qora.Billing.UnitTests.Infrastructure.Caching;

/// <summary>
/// Cubre el seam de cache (redis-ready-cache-limiter): selección de provider en DI, validación
/// fail-fast y compatibilidad zero-config. Ninguna prueba abre un socket hacia Redis: la rama Redis
/// se verifica por el tipo del <see cref="ServiceDescriptor"/> registrado, no construyendo la conexión.
/// </summary>
public class CacheServiceCollectionTests
{
    private static IConfiguration BuildConfig(params (string Key, string? Value)[] entries)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
    }

    // Scenario: "default resuelve a MemoryDistributedCache" + "sin sección Cache → InMemory por defecto"
    [Fact]
    public void AddCachingServices_WithNoCacheSection_ResolvesMemoryDistributedCache()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(); // sin sección Cache

        services.AddCachingServices(configuration);
        using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDistributedCache>();
        cache.GetType().Should().Be(typeof(MemoryDistributedCache));
    }

    // Scenario: "Redis registra el cache StackExchange" + "default no intenta conectar a Redis"
    // Se asserta el tipo de implementación registrado SIN construir/abrir la conexión a Redis.
    [Fact]
    public void AddCachingServices_WithRedisProviderAndConnectionString_RegistersRedisCacheImplType()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(
            ("Cache:Provider", "Redis"),
            ("Cache:RedisConnectionString", "localhost:6379"),
            ("Cache:InstanceName", "qora:"));

        services.AddCachingServices(configuration);

        // Redis 9.x registra el tipo interno RedisCacheImpl como implementación de IDistributedCache.
        // Se asserta por namespace de StackExchangeRedis (no se abre socket) en vez de un tipo público.
        var descriptor = services.Single(d => d.ServiceType == typeof(IDistributedCache));
        descriptor.ImplementationType.Should().NotBeNull();
        descriptor.ImplementationType!.Namespace
            .Should().Be(typeof(RedisCache).Namespace, "el provider Redis registra el cache de StackExchangeRedis");
        descriptor.ImplementationType.Should().NotBe(typeof(MemoryDistributedCache));
    }

    // Scenario: "Redis registra el cache StackExchange" — InstanceName se aplica como prefijo de claves.
    [Fact]
    public void AddCachingServices_WithRedisProvider_AppliesInstanceNameToRedisOptions()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(
            ("Cache:Provider", "Redis"),
            ("Cache:RedisConnectionString", "localhost:6379"),
            ("Cache:InstanceName", "qora:"));

        services.AddCachingServices(configuration);
        using var provider = services.BuildServiceProvider();

        var redisOptions = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        redisOptions.InstanceName.Should().Be("qora:");
        redisOptions.Configuration.Should().Be("localhost:6379");
    }

    // Scenario: "Provider inválido" — un valor de Provider fuera del enum debe fallar el binding.
    [Fact]
    public void AddCachingServices_WithInvalidProvider_FailsToBind()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(("Cache:Provider", "Memcached"));

        // El binding de un enum desconocido lanza al leer la sección al seleccionar el provider.
        var act = () => services.AddCachingServices(configuration);

        act.Should().Throw<InvalidOperationException>();
    }

    // Scenario: "Redis sin connection string falla al arrancar" (fail-fast, no diferido).
    [Fact]
    public void Validator_WhenRedisWithoutConnectionString_ReturnsFail()
    {
        var validator = new CacheOptionsValidator();
        var options = new CacheOptions { Provider = CacheProvider.Redis, RedisConnectionString = null };

        var result = validator.Validate("Cache", options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RedisConnectionString"));
    }

    [Fact]
    public void Validator_WhenRedisWithEmptyConnectionString_ReturnsFail()
    {
        var validator = new CacheOptionsValidator();
        var options = new CacheOptions { Provider = CacheProvider.Redis, RedisConnectionString = "   " };

        var result = validator.Validate("Cache", options);

        result.Failed.Should().BeTrue();
    }

    // Scenario: validator pass — InMemory sin connection string.
    [Fact]
    public void Validator_WhenInMemoryWithoutConnectionString_ReturnsSuccess()
    {
        var validator = new CacheOptionsValidator();
        var options = new CacheOptions { Provider = CacheProvider.InMemory };

        var result = validator.Validate("Cache", options);

        result.Succeeded.Should().BeTrue();
    }

    // Scenario: validator pass — Redis con connection string.
    [Fact]
    public void Validator_WhenRedisWithConnectionString_ReturnsSuccess()
    {
        var validator = new CacheOptionsValidator();
        var options = new CacheOptions { Provider = CacheProvider.Redis, RedisConnectionString = "localhost:6379" };

        var result = validator.Validate("Cache", options);

        result.Succeeded.Should().BeTrue();
    }

    // Scenario: "Redis sin connection string falla al arrancar" — exigido al construir el provider
    // con ValidateOnStart-style resolution (IValidateOptions registrado por AddCachingServices).
    [Fact]
    public void AddCachingServices_RedisWithoutConnectionString_FailsWhenOptionsValidated()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(("Cache:Provider", "Redis"));

        services.AddCachingServices(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    // Scenario: "registro idempotente" — AddCachingServices y luego AddInfrastructureServices
    // dejan una sola registración efectiva de IDistributedCache.
    [Fact]
    public void Registration_IsIdempotent_SingleEffectiveDistributedCache()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig();

        services.AddCachingServices(configuration);
        services.AddInfrastructureServices(configuration);
        using var provider = services.BuildServiceProvider();

        // El service provider resuelve exactamente UNA instancia de IDistributedCache (la última gana,
        // y AddDistributedMemoryCache usa TryAdd → no duplica el descriptor activo).
        var caches = provider.GetServices<IDistributedCache>().ToList();
        caches.Should().ContainSingle();
        caches.Single().GetType().Should().Be(typeof(MemoryDistributedCache));
    }

    // Scenario: "endpoints sin cambio de comportamiento" / backward-compat — boot zero-config.
    [Fact]
    public void ZeroConfig_ResolvesDistributedCache_WithoutThrowing()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfig(); // config totalmente vacía

        var act = () =>
        {
            services.AddCachingServices(configuration);
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IDistributedCache>();
        };

        act.Should().NotThrow();
    }
}
