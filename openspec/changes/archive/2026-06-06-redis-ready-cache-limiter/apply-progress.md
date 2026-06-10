# Apply Progress: redis-ready-cache-limiter

**Status:** Complete — 19/19 tasks. cache seam only (D1 limiter deferred, D2 dormant Redis NuGet, D3 no consumer, D4 single `Cache` section default InMemory).
**Mode:** Standard (config.yaml `apply.tdd: false`; tests written alongside).
**Build/Test:** `dotnet build` green (0 warn / 0 err). `dotnet test` UnitTests green — 458 passed, 0 failed (11 new caching tests + 447 existing).

## Files changed

| File | Action | What |
|------|--------|------|
| `src/Qora.Billing.Application/Settings/CacheOptions.cs` | Create | `enum CacheProvider {InMemory,Redis}` + `CacheOptions` (`SectionName="Cache"`, `Provider=InMemory`, `RedisConnectionString?`, `InstanceName="qora:"`, `[Range(1,int.MaxValue)] DefaultTtlSeconds=300`) |
| `src/Qora.Billing.Infrastructure/Caching/CacheOptionsValidator.cs` | Create | `IValidateOptions<CacheOptions>`: Redis ⇒ non-empty conn string else Fail; mirrors `SriConfigurationValidator` |
| `src/Qora.Billing.Infrastructure/Qora.Billing.Infrastructure.csproj` | Modify | Dormant `Microsoft.Extensions.Caching.StackExchangeRedis` `Version="9.*"` (MIT), mirrors EF/Npgsql floating `9.*` |
| `src/Qora.Billing.Infrastructure/DependencyInjection.cs` | Modify | New `AddCachingServices(this IServiceCollection, IConfiguration)`; `Configure<CacheOptions>` + `AddSingleton<IValidateOptions<CacheOptions>, CacheOptionsValidator>()`; branch on bound Provider → `AddDistributedMemoryCache()` / `AddStackExchangeRedisCache(o=>{Configuration; InstanceName;})`. Called once inside `AddInfrastructureServices` right after `AddSriClientWithResilience`. |
| `src/Qora.Billing.Api/appsettings.json` | Modify | `Cache` section (sibling of `RateLimit`/`Encryption`): Provider=InMemory, RedisConnectionString=null, InstanceName="qora:", DefaultTtlSeconds=300 |
| `docker-compose.yml` | Modify | Commented `redis:7-alpine` service + commented `Cache__Provider`/`Cache__RedisConnectionString` env on billing-api, mirroring commented Traefik block |
| `tests/Qora.Billing.UnitTests/Infrastructure/Caching/CacheServiceCollectionTests.cs` | Create | 11 tests, one+ per spec scenario |

## Decisions / divergences

- **Task 1.2 placement (documented divergence):** Followed the design's File Changes table — `CacheOptions` in `Application/Settings/`, `CacheOptionsValidator` in `Infrastructure/Caching/`. This DIVERGES from the existing `SriConfiguration`+`SriConfigurationValidator` co-location (both in `Infrastructure/Sri/`). Kept the design's split because other settings (Idempotency, Lifecycle, Emission, etc.) already live in `Application/Settings/`; the validator stays in Infrastructure since it has no Application-layer home and mirrors the Sri validator pattern.
- **InstanceName default = `"qora:"`** (resolved open question).
- **Redis impl type assertion:** Redis 9.x registers the internal `RedisCacheImpl` (not the public `RedisCache`) as `IDistributedCache`. Test asserts the descriptor `ImplementationType.Namespace` equals the StackExchangeRedis namespace AND is not `MemoryDistributedCache` — verified WITHOUT building the provider / opening a socket.
- **FluentAssertions 8 quirk:** `IDistributedCache.Should()` resolved to the enum overload (CS0453). Used `cache.GetType().Should().Be(typeof(...))` instead of `BeOfType`.
- **Fail-fast test:** `provider.GetRequiredService<IOptions<CacheOptions>>().Value` throws `OptionsValidationException` when Provider=Redis with empty conn string (validator runs on `.Value`), proving startup-time fail-fast rather than deferred.

## Scenario → test coverage

- sin sección Cache → InMemory + default resuelve a MemoryDistributedCache → `AddCachingServices_WithNoCacheSection_ResolvesMemoryDistributedCache`
- Redis registra el cache StackExchange + InstanceName prefijo → `..._RegistersRedisCacheImplType`, `..._AppliesInstanceNameToRedisOptions`
- default no intenta conectar a Redis → covered by type-only assertion (no socket) + zero-config test
- Provider inválido → `..._WithInvalidProvider_FailsToBind`
- Redis sin conn string falla al arrancar → `Validator_WhenRedisWithoutConnectionString_ReturnsFail`, `..._WithEmptyConnectionString_ReturnsFail`, `AddCachingServices_RedisWithoutConnectionString_FailsWhenOptionsValidated`
- validator pass → `Validator_WhenInMemoryWithoutConnectionString_ReturnsSuccess`, `Validator_WhenRedisWithConnectionString_ReturnsSuccess`
- registro idempotente → `Registration_IsIdempotent_SingleEffectiveDistributedCache`
- endpoints sin cambio / zero-config boot → `ZeroConfig_ResolvesDistributedCache_WithoutThrowing`

## Risks

- Floating `9.*` on the Redis package: a future 9.x bump could rename/move the internal impl type; the test asserts by namespace (more stable than the type name) to reduce churn.
- No live-Redis integration test (by design — no consumer wired yet).
