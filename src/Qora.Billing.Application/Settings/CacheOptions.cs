using System.ComponentModel.DataAnnotations;

namespace Qora.Billing.Application.Settings;

/// <summary>
/// Provider de cache distribuido seleccionable por configuración.
/// </summary>
public enum CacheProvider
{
    /// <summary>Cache distribuido en proceso (<c>AddDistributedMemoryCache</c>). Default.</summary>
    InMemory,

    /// <summary>Cache distribuido sobre Redis (<c>AddStackExchangeRedisCache</c>).</summary>
    Redis,
}

/// <summary>
/// Configuración del seam de cache distribuido (<see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>).
/// Vinculada a la sección "Cache" en appsettings. Por defecto <see cref="CacheProvider.InMemory"/>,
/// de modo que la ausencia total de la sección preserva el comportamiento actual (sin Redis en runtime).
/// </summary>
public class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Provider del cache: <see cref="CacheProvider.InMemory"/> (default) o <see cref="CacheProvider.Redis"/>.
    /// Un valor no perteneciente al enum es inválido y aborta el binding/arranque.
    /// </summary>
    public CacheProvider Provider { get; set; } = CacheProvider.InMemory;

    /// <summary>
    /// Connection string de Redis (formato StackExchange.Redis). Requerido (no vacío) cuando
    /// <see cref="Provider"/> es <see cref="CacheProvider.Redis"/>; ignorado en <see cref="CacheProvider.InMemory"/>.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Prefijo de claves aplicado por Redis (<c>RedisCacheOptions.InstanceName</c>). Por defecto "qora:".
    /// Los consumidores futuros DEBEN además embeber el tenant id en la clave (p.ej. "t:{tenantId}:apikey:{hash}")
    /// para que un Redis compartido nunca cruce fronteras entre tenants (RLS no protege el cache).
    /// </summary>
    public string InstanceName { get; set; } = "qora:";

    /// <summary>
    /// TTL por defecto (en segundos) para entradas que no especifiquen uno propio. Por defecto 300.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "DefaultTtlSeconds debe ser mayor o igual a 1.")]
    public int DefaultTtlSeconds { get; set; } = 300;
}
