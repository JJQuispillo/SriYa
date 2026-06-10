# Design: Redis-ready cache seam (default in-memory)

## Technical Approach

Land the cache **provider seam only** (proposal D1–D4). Add a `CacheOptions` settings class in
`Application/Settings` + an `IValidateOptions<CacheOptions>` validator (mirroring
`SriConfigurationValidator`), and an `AddCachingServices(IServiceCollection, IConfiguration)`
extension in `Infrastructure/DependencyInjection.cs` that registers the framework's
`IDistributedCache`: `AddDistributedMemoryCache()` by default, `AddStackExchangeRedisCache(...)`
when `Cache:Provider=Redis`. The StackExchangeRedis NuGet ref is present but inert unless the flag
selects Redis. No consumer is wired (D3); the rate limiter is untouched (D1).

## Architecture Decisions

### Decision: Use built-in `IDistributedCache`, no custom interface (D2)

| Option | Tradeoff | Decision |
|--------|----------|----------|
| Built-in `IDistributedCache` | byte[]-oriented; standard; trivial Memory↔Redis swap; zero bespoke API | **Chosen** |
| Custom `IBillingCache` | typed API but reinvents framework, more code/tests | Rejected |
| `HybridCache` (.NET 9) | implies a distributed L2; heavier than "ready, default off" | Deferred |

**Rationale**: `AddDistributedMemoryCache()` and `AddStackExchangeRedisCache()` expose the identical
`IDistributedCache`, so future consumers depend on one stable abstraction and providers swap by config.

### Decision: Single `Cache` config section drives DI selection (D4)

**Choice**: One options section (`SectionName="Cache"`): `Provider` (InMemory|Redis, default InMemory),
`RedisConnectionString`, `InstanceName`, `DefaultTtlSeconds`. DI branches on `Provider`.
**Alternatives**: separate sections per backend (more config surface, no benefit). **Rationale**: mirrors
existing one-section-per-feature pattern (`Sri`, `RateLimit`, `Encryption`); one flag flips the backend.

### Decision: Fail-fast cross-property validation via `IValidateOptions`

**Choice**: `[Required]`/`[Range]` DataAnnotations for shape + `CacheOptionsValidator : IValidateOptions<CacheOptions>`
asserting `Provider=Redis` ⇒ non-empty `RedisConnectionString`. Registered as
`AddSingleton<IValidateOptions<CacheOptions>, CacheOptionsValidator>()`, runs at provider build.
**Rationale**: exact pattern of `SriConfigurationValidator`; a misconfigured Redis must crash at startup, not at first cache call.

### Decision: Dormant NuGet ref, floating-version pinned

**Choice**: Add `Microsoft.Extensions.Caching.StackExchangeRedis` Version `9.*` to
`Qora.Billing.Infrastructure.csproj`. **Rationale**: MIT-licensed (consistent with the repo's MIT-only
policy that pinned MediatR); repo has no central package management, so mirror the per-package floating
`9.*` used for the EF Core / Npgsql family. Inert at runtime unless `Provider=Redis`.

### Decision: Tenant key-namespacing guidance (no consumer yet)

**Choice**: Set `InstanceName` (default e.g. `qora:`) as the Redis key prefix; document that future
consumers MUST embed the tenant id in cache keys (e.g. `t:{tenantId}:apikey:{hash}`) so a shared Redis
never crosses tenant boundaries. **Rationale**: RLS does not protect a cache; isolation must be encoded
in the key. Captured now so the seam doesn't mislead a future consumer.

## Data Flow

    Configuration("Cache")
        │  bind + validate (fail-fast)
        ▼
    AddCachingServices ──Provider=InMemory──► AddDistributedMemoryCache()
        │                                         └─► IDistributedCache (in-proc)
        └────────────────Provider=Redis────────► AddStackExchangeRedisCache(cfg+InstanceName)
                                                  └─► IDistributedCache (Redis)
    (no consumer wired this change — DI registration only)

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `src/Qora.Billing.Application/Settings/CacheOptions.cs` | Create | Options: `Provider`, `RedisConnectionString`, `InstanceName`, `DefaultTtlSeconds`; `SectionName="Cache"`; DataAnnotations + `CacheProvider` enum |
| `src/Qora.Billing.Infrastructure/Caching/CacheOptionsValidator.cs` | Create | `IValidateOptions<CacheOptions>`: Redis ⇒ connection string required |
| `src/Qora.Billing.Infrastructure/DependencyInjection.cs` | Modify | Add `AddCachingServices(this IServiceCollection, IConfiguration)`; call it inside `AddInfrastructureServices`; `Configure<CacheOptions>` + register validator |
| `src/Qora.Billing.Infrastructure/Qora.Billing.Infrastructure.csproj` | Modify | Dormant `Microsoft.Extensions.Caching.StackExchangeRedis` `Version="9.*"` |
| `src/Qora.Billing.Api/appsettings.json` | Modify | `Cache` section (sibling of `RateLimit`/`Encryption`), `Provider:"InMemory"` |
| `docker-compose.yml` | Modify | Commented `redis` service + `Cache__*` env example, mirroring commented Traefik block |
| `tests/Qora.Billing.UnitTests/Infrastructure/Caching/CacheServiceCollectionTests.cs` | Create | DI provider-selection + validator tests |

## Interfaces / Contracts

```csharp
// Application/Settings/CacheOptions.cs
public enum CacheProvider { InMemory, Redis }
public class CacheOptions
{
    public const string SectionName = "Cache";
    public CacheProvider Provider { get; set; } = CacheProvider.InMemory;
    public string? RedisConnectionString { get; set; }
    public string InstanceName { get; set; } = "qora:";
    [Range(1, int.MaxValue)] public int DefaultTtlSeconds { get; set; } = 300;
}

// Infrastructure/DependencyInjection.cs (wired inside AddInfrastructureServices)
public static IServiceCollection AddCachingServices(
    this IServiceCollection services, IConfiguration configuration);
```

Wiring lives **inside** `AddInfrastructureServices` (a single `services.AddCachingServices(configuration);`
call near the existing `Configure<>` block), consistent with how `AddSriClientWithResilience` / `AddPdfServices`
compose. Selection reads the bound `CacheOptions.Provider`; on Redis, set
`options.Configuration = RedisConnectionString` and `options.InstanceName = InstanceName`.

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | Default selection | Empty/`InMemory` config → `AddCachingServices` resolves `IDistributedCache` as `MemoryDistributedCache` |
| Unit | Redis selection (no connect) | `Provider=Redis` + conn string → registered impl is `RedisCache` (assert descriptor/type; do NOT open a socket) |
| Unit | Validator pass | `InMemory` (no conn string) → `ValidateOptionsResult.Success` |
| Unit | Validator fail-fast | `Provider=Redis` + null/empty conn string → `Fail` |

Tests go in `tests/Qora.Billing.UnitTests` (the CI-gated project), package `Infrastructure/Caching/`, using
xUnit + FluentAssertions, building config via `ConfigurationBuilder().AddInMemoryCollection(...)` and a real
`ServiceCollection`. Validator can be exercised directly or via `ValidateOnStart`-style resolution. No live Redis.

## Migration / Rollout

No data migration. Config-only: default `Provider=InMemory` ⇒ behavior identical to today. Enabling Redis is
a config flip + reachable Redis.

## Rollback

Config-only — set/leave `Cache:Provider=InMemory` (the in-process `IDistributedCache`). Full revert =
remove the dormant NuGet ref + `AddCachingServices`; no consumer depends on it, no data to undo.

## Open Questions

- [ ] Default `InstanceName` value (`"qora:"` proposed) — confirm prefix at apply time.

## Deferred follow-up (out of scope, noted)

Distributed **rate limiter** is its own change. The built-in `System.Threading.RateLimiting` partitions are
in-process; a future change can back a fixed-window/token-bucket on this same `IDistributedCache`
(or recommend gateway-level limiting under scale). This seam does not preclude that — it provides the store.
