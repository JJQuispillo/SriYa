# Exploration: redis-ready-cache-limiter

> Make the API "Redis-ready" WITHOUT adding Redis now. Introduce a config-activatable
> abstraction so cache and rate-limiting can switch from in-memory (default) to a
> distributed backend (Redis) when the service is scaled horizontally. No Redis
> dependency is added unless a flag enables it.

## Current State

The API is a single-instance .NET 9 microservice today (one `billing-api` + one Postgres
in `docker-compose.yml`, no `replicas`/`deploy`, only commented Traefik labels). Several
pieces of state are **per-process** and would become incorrect under multiple replicas:

1. **Rate limiter (the real break).** `Program.cs` (~L149-179) wires
   `builder.Services.AddRateLimiter` with `AddSlidingWindowLimiter("api-key-policy", …)`
   bound to `RateLimit:PermitLimit` (120) and `RateLimit:WindowSeconds` (60). Applied via
   `app.UseRateLimiter()` (L278) and `.RequireRateLimiting("api-key-policy")` on document /
   certificate / api-key / lifecycle / bootstrap endpoint groups. The built-in
   `System.Threading.RateLimiting` partitions live **in-process only** — with N replicas the
   effective limit becomes N×120/min, silently defeating the throttle.

2. **Per-request API-key lookup (cache candidate, not a correctness break).**
   `ApiKeyAuthenticationHandler` (L50) calls `_apiKeyRepository.GetByKeyHashAsync(keyHash)`
   on **every** authenticated request — an uncached DB round-trip. Caching this with a short
   TTL would cut DB load; under horizontal scale you'd want that cache distributed (or just
   keep it per-instance with a short TTL — both acceptable). This is the natural home for a
   general read cache for tenant/api-key/cert metadata.

3. **No cache abstraction exists.** Grep confirms **no** `IMemoryCache`, `IDistributedCache`,
   `HybridCache`, `AddMemoryCache`, `ConcurrentDictionary`-as-cache, or `StackExchangeRedis`
   anywhere in `src`. The only static `Dictionary` is `Document.ValidTransitions` — a domain
   state-machine constant, not shared mutable state. So the inventory of scale-breaking
   in-memory state is exactly: **the rate limiter** (and the *opportunity* to add a cache).

**Already safe under horizontal scale (do not touch):**
- **Fiscal secuencial** — atomic via Postgres `SELECT … FOR UPDATE`
  (`GetMaxSecuencialWithLockAsync`, migration `B6_EmissionAtomicity`). Correct as-is.
- **Idempotency** — `IIdempotencyStore` is DB-backed (`IdempotencySettings`), not in-memory.
- **Background services** — `SriRetryService`, `SriReconciliationService`,
  `RidePdfRetryService` are already cross-pod safe via `FOR UPDATE SKIP LOCKED`
  ("sin necesidad de leader election", per their own doc comments). No Redis needed for
  coordination.
- **Tenant isolation** — Postgres RLS + GUC `app.current_tenant`, request-scoped. Unaffected.

## Affected Areas

- `src/Qora.Billing.Api/Program.cs` (~L149-179 rate-limiter wiring, L278 `UseRateLimiter`) —
  the limiter registration must move behind a provider switch.
- `src/Qora.Billing.Infrastructure/DependencyInjection.cs` (`AddInfrastructureServices`) —
  natural home for a new `AddCachingServices(configuration)` / `AddDistributedRateLimiting`
  extension that reads the provider flag and registers the right services. Follows the
  existing `services.Configure<XOptions>(configuration.GetSection(...))` + dedicated
  `Add*` extension pattern already used for SRI client, PDF, email.
- `src/Qora.Billing.Application/Settings/` — add a `CacheSettings`/`CacheOptions` class
  mirroring the existing options-class convention (`const string SectionName`,
  DataAnnotations `[Range]`/`[Required]`, XML doc). E.g. `RidePdfRetryOptions.cs` is the
  template.
- `src/Qora.Billing.Api/appsettings.json` — add a `Cache` section (sibling of `RateLimit`,
  `Encryption`); keep `RateLimit` for limits, add provider selector + optional Redis conn.
- `src/Qora.Billing.Api/Middleware/ApiKeyAuthenticationHandler.cs` — *optional* consumer if
  the cache seam is wired into api-key resolution (could be deferred to a follow-up).
- `docker-compose.yml` — optional commented `redis` service + env example, mirroring the
  commented Traefik block (documentation only; not enabled by default).

## Approaches

### A) Cache abstraction — built-in `IDistributedCache` (RECOMMENDED)
Use the framework's `IDistributedCache`:
- Default (`Cache:Provider = InMemory`): `services.AddDistributedMemoryCache()` — an
  in-process `IDistributedCache` impl, **zero new dependencies**, same interface.
- Enabled (`Cache:Provider = Redis`): `services.AddStackExchangeRedisCache(o => o.Configuration = connStr)`.
  The `Microsoft.Extensions.Caching.StackExchangeRedis` package reference can be added but
  is only *touched* when the flag selects Redis (it's a transitive NuGet ref, not a runtime
  Redis dependency — nothing connects unless `Provider = Redis`).
- Pros: standard, no custom interface to maintain, callers depend only on `IDistributedCache`,
  trivial swap, byte[]-based so works for serialized tenant/api-key/cert metadata.
- Cons: `IDistributedCache` is byte-oriented (need small serialization helper); the
  StackExchangeRedis NuGet ref is present in the csproj even when unused (acceptable —
  contradicts "no Redis dependency added" only at the *package-reference* level, never at
  runtime). If even the package ref is unacceptable, gate it behind an MSBuild condition or
  a separate optional assembly loaded reflectively (adds complexity — not recommended).
- Effort: **Low**.

### A') Cache abstraction — custom `IBillingCache` interface
Define our own interface, two impls (memory / redis).
- Pros: full control of API shape (typed get/set, no manual byte serialization), can hide the
  Redis package entirely behind a conditionally-compiled/loaded impl → satisfies "no Redis
  dependency" literally.
- Cons: reinvents `IDistributedCache`; more code + tests to maintain; team must learn a
  bespoke abstraction. Net new value over A is marginal.
- Effort: **Medium**.

> .NET 9 also offers `HybridCache` (L1+L2). Tempting, but it implies a distributed L2 and is
> newer/heavier than needed for "be ready, default off". Note as a future option; do not adopt now.

### B) Distributed rate limiter — the hard part (be honest)
The built-in `RateLimiter` partitions are **strictly in-process**; there is no official
distributed backing store. Options, in order of risk:

1. **Custom `IDistributedCache`-backed sliding/fixed window** via
   `PartitionedRateLimiter.Create(...)` with a custom `RateLimiter` that increments a Redis
   counter (atomic INCR + EXPIRE / Lua). True sliding window is non-trivial to do atomically
   and correctly; a **fixed-window or token-bucket on Redis** is the pragmatic, well-trodden
   form. Effort: **Medium-High**, correctness risk on edge cases (clock skew, expiry races).
2. **Third-party package** (e.g. a Redis rate-limiting lib). Adds a dependency + supply-chain
   surface; must vet license (repo deliberately avoids non-MIT/non-free deps — MediatR was
   pinned to the last MIT version for this reason). Effort: **Low-Medium** but dependency cost.
3. **Reverse-proxy / gateway rate limiting** (Traefik/NGINX/Envoy in front). Moves the concern
   out of the app entirely and is how many horizontally-scaled deployments actually solve it.
   Effort: **Low** (ops config), **zero app code**, but out of this service's control.

**Honest assessment:** A "Redis-ready cache" is genuinely Low effort. A "Redis-ready *rate
limiter*" is **materially harder** and riskier. Recommend decoupling them: ship the cache
abstraction + provider switch now, and for the limiter make it **provider-aware but ship the
distributed backend as a clearly-scoped follow-up** (or document gateway-level limiting as the
recommended path under scale). Don't let the limiter complexity block the cheap, high-value
cache seam.

### C) Config switch design
Single options section drives DI:
```jsonc
"Cache": {
  "Provider": "InMemory",          // InMemory | Redis
  "RedisConnectionString": "",     // required only when Provider = Redis
  "InstanceName": "sriya:",        // key prefix
  "DefaultTtlSeconds": 60
}
```
- `CacheOptions` class in `Application/Settings` (SectionName `"Cache"`), validated with
  DataAnnotations + an `IValidateOptions<CacheOptions>` that **fails fast** if
  `Provider = Redis` and no connection string (mirrors `SriConfigurationValidator`).
- New `AddCachingServices(this IServiceCollection, IConfiguration)` extension in
  `Infrastructure/DependencyInjection.cs`: reads `Cache:Provider`, registers
  `AddDistributedMemoryCache()` or `AddStackExchangeRedisCache(...)` accordingly, called from
  `AddInfrastructureServices`.
- Rate limiter: keep `RateLimit:PermitLimit`/`WindowSeconds`; if/when distributed limiter is
  implemented, gate its store on the same `Cache:Provider` so one flag governs both.

## Recommendation

1. **Cache: Approach A — built-in `IDistributedCache`.** `AddDistributedMemoryCache()` by
   default (no runtime Redis), `AddStackExchangeRedisCache` when `Cache:Provider = Redis`.
   Lowest effort, standard, swappable, satisfies "Redis-ready, default in-memory." Accept the
   package reference being present-but-dormant; if the team insists on *zero* package ref,
   fall back to A' (custom interface) — but that's extra cost for little gain.
2. **Rate limiter: decouple.** Make the registration provider-aware but treat the actual
   **distributed limiter algorithm as a scoped follow-up change** (custom fixed-window/
   token-bucket over `IDistributedCache`, option B.1), and meanwhile document gateway-level
   limiting (B.3) as the supported horizontal-scale answer. Be explicit in the proposal that
   "Redis-ready limiter" ≠ "Redis-ready cache" in effort/risk.
3. **Config: single `Cache` section** + fail-fast validator, wired through a new
   `AddCachingServices` Infrastructure extension. One flag, consistent with existing options
   conventions.
4. **First cache consumer:** the api-key resolution path (`GetByKeyHashAsync`) is the obvious
   high-value first use, but wiring it can be a separate task so the abstraction lands first.

## Risks

- **Scope creep on the limiter.** Distributed sliding-window correctness (atomicity, expiry
  races, clock skew) can swallow the change. Mitigate by scoping the distributed limiter
  separately and shipping the cache abstraction independently.
- **"No Redis dependency" interpretation.** Approach A adds a *NuGet package reference* that is
  inert at runtime unless the flag is on. If the requirement means literally no package ref,
  this must be resolved in the proposal (→ A' custom interface, or MSBuild-conditional ref).
- **Cache invalidation / staleness on the api-key path.** Caching api-key lookups can keep a
  revoked/rotated key valid for the TTL. Need a short TTL and/or invalidation on
  rotate/revoke. Treat carefully if/when that consumer is wired.
- **Encrypted cert metadata in a shared cache.** Anything cached that derives from encrypted
  fields (certificate/signature metadata) must not leak plaintext into Redis; cache only
  non-sensitive projections. Multi-tenant key namespacing (`InstanceName` + tenant id) is
  mandatory to avoid cross-tenant bleed.
- **Operational surface.** Enabling Redis adds a component to run/secure/back-up. Keep default
  off; document the opt-in clearly (compose example commented, like Traefik).

## Out of Scope (mention as related, exclude here)

- **Fiscal secuencial** — stays in Postgres `FOR UPDATE` (correct; never move to Redis).
- **Idempotency store** migration — already DB-backed; no change.
- **Async SRI outbox/queue** — a separate future change; not part of "Redis-ready cache/limiter."
- **Background-service coordination** — already cross-pod safe via `SKIP LOCKED`; no leader
  election / Redis lock needed.

## Open Questions for Proposal / Design

1. **Package-ref strictness:** Does "no Redis dependency unless flag enables it" permit a
   dormant `Microsoft.Extensions.Caching.StackExchangeRedis` NuGet reference (Approach A), or
   must even the package ref be absent (→ Approach A' / conditional MSBuild ref)?
2. **Limiter scope this change:** Ship a working distributed rate limiter now (custom
   fixed-window/token-bucket over `IDistributedCache`, Medium-High effort/risk), or land the
   cache abstraction + provider-aware *seam* now and defer the distributed limiter algorithm
   (recommending gateway-level limiting in the interim)?
3. **First cache consumer:** Wire the api-key `GetByKeyHashAsync` cache as part of this change,
   or land the abstraction alone and add consumers later? If wired, what TTL + invalidation on
   key rotation/revocation?
4. **Config grouping:** One `Cache` section governing both cache and limiter store, or separate
   `Cache` and `RateLimit:Store` selectors? (Recommend one flag for operator simplicity.)
5. **Custom interface vs `IDistributedCache`:** Confirm A over A' (affects every call site and
   the package-ref question).

## Ready for Proposal

Yes. Scope is clear and the codebase is verified. The proposal must resolve the 5 open
questions above — chiefly (1) package-ref strictness and (2) whether the distributed *limiter*
is in-scope now or deferred, since that single decision drives most of the effort and risk.
