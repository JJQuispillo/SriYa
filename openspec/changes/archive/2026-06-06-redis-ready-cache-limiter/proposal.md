# Proposal: Redis-ready cache seam (default in-memory)

## Intent

Make the .NET 9 API horizontally-scale ready by introducing a config-activatable distributed cache, without adding a runtime Redis dependency. Today the only scale-breaking in-process state is the rate limiter (out of scope here); all other state (fiscal secuencial, idempotency, background coordination, tenant RLS) is already cross-pod safe. This change lands the cache **abstraction + provider seam only** so future consumers can opt into a distributed backend by flipping one flag.

## Scope

### In Scope
- `Cache` options class in `Application/Settings` (`SectionName="Cache"`): `Provider` (InMemory|Redis, default InMemory), `RedisConnectionString`, `InstanceName`, `DefaultTtlSeconds`; DataAnnotations + `IValidateOptions<CacheOptions>` fail-fast (Redis requires connection string), mirroring `SriConfigurationValidator`.
- `AddCachingServices(this IServiceCollection, IConfiguration)` in `Infrastructure/DependencyInjection.cs`: registers `AddDistributedMemoryCache()` by default, `AddStackExchangeRedisCache(...)` when `Provider=Redis`. Wired into `AddInfrastructureServices`.
- Dormant NuGet ref `Microsoft.Extensions.Caching.StackExchangeRedis` (MIT) — inert unless `Provider=Redis`.
- Docs: `appsettings.json` `Cache` sample + commented `redis` service in `docker-compose.yml` (mirrors existing commented Traefik block).
- xUnit tests for DI/options provider selection + validator.

### Out of Scope (related future work)
- **Distributed rate limiter** — named follow-up change. Interim: recommend gateway-level limiting (Traefik/NGINX) under scale.
- **Cache consumers** (e.g. api-key `GetByKeyHashAsync`) — deferred; abstraction lands alone.
- Fiscal secuencial (stays Postgres `FOR UPDATE`), idempotency store, async SRI outbox/queue, background coordination — all already safe or separate changes.

## Approach

Use the built-in `IDistributedCache` directly at call sites — no custom interface (D2). One `Cache` section governs provider selection (D4). DI extension reads `Cache:Provider` and registers the matching impl. `AddDistributedMemoryCache()` keeps zero runtime Redis by default; the StackExchangeRedis package is referenced but only touched when the flag selects Redis.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Application/Settings/CacheOptions.cs` | New | Options + validator |
| `Infrastructure/DependencyInjection.cs` | Modified | `AddCachingServices` + wire-in |
| `Infrastructure/*.csproj` | Modified | Dormant Redis NuGet ref |
| `Api/appsettings.json` | Modified | `Cache` section |
| `docker-compose.yml` | Modified | Commented `redis` service |
| `tests/UnitTests` | New | DI/options/validator tests |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| "No Redis dep" read as no package ref | Med | D2 locks dormant ref (MIT, inert); documented |
| Scope creep into limiter | Med | D1 defers limiter to own change |
| Premature consumer wiring | Low | D3 ships abstraction only |

## Rollback Plan

Trivial / config-only. Default `Provider=InMemory` leaves runtime behavior unchanged. Reverting is removing the dormant NuGet ref + DI extension; no data migration, no consumer depends on it.

## Dependencies

- NuGet `Microsoft.Extensions.Caching.StackExchangeRedis` (MIT) — dormant.

## Success Criteria

- [ ] Default boot uses `AddDistributedMemoryCache()`; no Redis connection attempted.
- [ ] `Provider=Redis` without connection string fails fast at startup.
- [ ] `Provider=Redis` with connection string registers `AddStackExchangeRedisCache`.
- [ ] `dotnet build` and `dotnet test` pass; no behavior change out of the box.
