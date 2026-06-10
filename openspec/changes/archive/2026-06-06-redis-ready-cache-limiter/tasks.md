# Tasks: Redis-ready cache seam (default in-memory)

Scope: cache seam only (D1 defer limiter, D2 built-in `IDistributedCache`, D3 no consumer,
D4 single `Cache` section default `InMemory`). Tests use `dotnet build` / `dotnet test`.

## Phase 1: Foundation (options + validator)

- [x] 1.1 Create `src/Qora.Billing.Application/Settings/CacheOptions.cs` with `enum CacheProvider { InMemory, Redis }` and `CacheOptions` (`SectionName="Cache"`, `Provider=InMemory`, `RedisConnectionString?`, `InstanceName="qora:"`, `[Range(1,int.MaxValue)] DefaultTtlSeconds=300`). Satisfies spec "CacheOptions y selección de provider" + design Interfaces.
- [x] 1.2 Decision sub-task: design splits options (Application/Settings) from validator (Infrastructure/Caching) but existing `SriConfiguration`+`SriConfigurationValidator` both live in `Infrastructure/Sri/`. Follow design's File Changes table (Application/Settings + Infrastructure/Caching); record divergence from the SriConfiguration co-location in apply notes.
- [x] 1.3 Create `src/Qora.Billing.Infrastructure/Caching/CacheOptionsValidator.cs` as `IValidateOptions<CacheOptions>` mirroring `Sri/SriConfigurationValidator.cs`: `Provider=Redis` ⇒ non-empty `RedisConnectionString` else `Fail`; else `Success`. Satisfies spec "Validación fail-fast de Redis".

## Phase 2: Infrastructure (NuGet ref)

- [x] 2.1 Add dormant `<PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="9.*" />` to `src/Qora.Billing.Infrastructure/Qora.Billing.Infrastructure.csproj`, mirroring the floating `9.*` EF/Npgsql family. Satisfies spec "NuGet Redis dormido" + design D "Dormant NuGet ref".

## Phase 3: Wiring (DI extension)

- [x] 3.1 In `src/Qora.Billing.Infrastructure/DependencyInjection.cs` add `public static IServiceCollection AddCachingServices(this IServiceCollection services, IConfiguration configuration)`: bind/`Configure<CacheOptions>` from `GetSection(CacheOptions.SectionName)`, `AddSingleton<IValidateOptions<CacheOptions>, CacheOptionsValidator>()`, branch on bound `Provider` → `AddDistributedMemoryCache()` (InMemory) or `AddStackExchangeRedisCache(o => { o.Configuration = RedisConnectionString; o.InstanceName = InstanceName; })` (Redis). Idempotent single `IDistributedCache`. Satisfies spec "AddCachingServices".
- [x] 3.2 Call `services.AddCachingServices(configuration);` once inside `AddInfrastructureServices` near the existing `Configure<>` block (after line ~125, alongside `AddSriClientWithResilience`/`AddPdfServices`). Satisfies spec idempotent-registration scenario.

## Phase 4: Config samples

- [x] 4.1 Add `Cache` section to `src/Qora.Billing.Api/appsettings.json` as sibling of `RateLimit`/`Encryption`: `Provider:"InMemory"`, `RedisConnectionString:null`, `InstanceName:"qora:"`, `DefaultTtlSeconds:300`. Satisfies design File Changes.
- [x] 4.2 Add a commented `redis` service + `Cache__Provider`/`Cache__RedisConnectionString` env example to `docker-compose.yml`, mirroring the commented Traefik block. Satisfies design File Changes.

## Phase 5: Testing (one test per spec scenario)

- [x] 5.1 Create `tests/Qora.Billing.UnitTests/Infrastructure/Caching/CacheServiceCollectionTests.cs` (xUnit + FluentAssertions, `ConfigurationBuilder().AddInMemoryCollection(...)` + real `ServiceCollection`).
- [x] 5.2 Test: no `Cache` section → `AddCachingServices` → resolved `IDistributedCache` is `MemoryDistributedCache`. Scenario "default resuelve a MemoryDistributedCache" + "sin sección Cache → InMemory".
- [x] 5.3 Test: `Provider=Redis` + non-empty conn string → service descriptor impl type is `RedisCache` (assert descriptor/type, do NOT open a socket / build no Redis connection). Scenario "Redis registra el cache StackExchange" + "default no intenta conectar".
- [x] 5.4 Test: `Provider="Memcached"` (invalid enum) → binding/validation fails. Scenario "Provider inválido".
- [x] 5.5 Test: `Provider=Redis` + null/empty conn string → `CacheOptionsValidator.Validate` returns `Fail` (fail-fast at startup, not deferred). Scenario "Redis sin connection string falla al arrancar".
- [x] 5.6 Test: `Provider=InMemory` (no conn string) → validator returns `ValidateOptionsResult.Success`; `Provider=Redis`+conn string → `Success`. Scenarios validator pass.
- [x] 5.7 Test: invoke `AddCachingServices` then `AddInfrastructureServices` → exactly one effective `IDistributedCache` registration (idempotent). Scenario "registro idempotente".
- [x] 5.8 Test: zero-config boot — `ServiceCollection` with empty config resolves `IDistributedCache` without throwing; no consumer wired (api-key `GetByKeyHashAsync` path unchanged). Scenario "endpoints sin cambio" / backward-compat.

## Phase 6: Verification

- [x] 6.1 Run `dotnet build` — must be green (new NuGet restores, no compile errors).
- [x] 6.2 Run `dotnet test` (Qora.Billing.UnitTests) — all new + existing tests green.
- [x] 6.3 Confirm app boots with NO `Cache` config (default `InMemory`) and zero Redis I/O; resolve `InstanceName` open question (`"qora:"`) at apply time.
