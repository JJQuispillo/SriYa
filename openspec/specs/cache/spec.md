# cache Specification (Redis-ready cache seam)

## Purpose

Sembrar en la .NET 9 API un **seam de cache distribuido activable por configuración** sin
introducir dependencia de Redis en runtime. Define las `CacheOptions`, su validación fail-fast
y el registro DI de `IDistributedCache` (in-memory por defecto, Redis al activar el flag). NO
incluye limiter distribuido ni ningún consumidor de cache (decisiones D1/D3).

## Requirements

### Requirement: CacheOptions y selección de provider

El sistema MUST exponer una sección de configuración `Cache` (`SectionName = "Cache"`) con
`Provider` (`InMemory` | `Redis`, default `InMemory`), `RedisConnectionString`, `InstanceName`
y `DefaultTtlSeconds`. Ausencia total de la sección MUST resolver a `InMemory` con el
comportamiento actual intacto. Un valor de `Provider` distinto de `InMemory`/`Redis` MUST ser
inválido.

#### Scenario: sin sección Cache → InMemory por defecto

- GIVEN un `appsettings` sin sección `Cache`
- WHEN la app enlaza `CacheOptions`
- THEN `Provider` MUST resolver a `InMemory` y el arranque MUST completar sin error

#### Scenario: Provider inválido

- GIVEN `Cache:Provider = "Memcached"`
- WHEN la app valida `CacheOptions` al arranque
- THEN la validación MUST fallar y el arranque MUST abortar

### Requirement: Validación fail-fast de Redis

El sistema MUST validar `CacheOptions` vía `IValidateOptions<CacheOptions>` (espejando
`SriConfigurationValidator`). Cuando `Provider = Redis`, `RedisConnectionString` MUST ser no
vacío; en caso contrario la validación MUST lanzar al arranque (fail-fast), NO de forma diferida
en el primer uso.

#### Scenario: Redis sin connection string falla al arrancar

- GIVEN `Cache:Provider = "Redis"` y `RedisConnectionString` vacío o ausente
- WHEN la app construye y valida los options al arranque
- THEN la validación MUST lanzar (`OptionsValidationException` o equivalente) abortando el arranque

#### Scenario: Redis con connection string es válido

- GIVEN `Cache:Provider = "Redis"` y `RedisConnectionString` no vacío
- WHEN la app valida `CacheOptions`
- THEN la validación MUST pasar sin error

### Requirement: AddCachingServices registra el IDistributedCache correcto

El sistema MUST exponer `AddCachingServices(this IServiceCollection, IConfiguration)` en
`Infrastructure/DependencyInjection.cs`, cableado dentro de `AddInfrastructureServices`. MUST
registrar `AddDistributedMemoryCache()` cuando `Provider = InMemory` y
`AddStackExchangeRedisCache(...)` cuando `Provider = Redis`. El registro MUST ser idempotente
(una sola registración efectiva de `IDistributedCache`). La app MUST arrancar con cero
configuración de cache.

#### Scenario: default resuelve a MemoryDistributedCache

- GIVEN una `ServiceCollection` sin sección `Cache`
- WHEN se invoca `AddCachingServices` y se construye el provider
- THEN `IDistributedCache` MUST resolver a `MemoryDistributedCache`

#### Scenario: Redis registra el cache StackExchange

- GIVEN `Cache:Provider = "Redis"` con `RedisConnectionString` no vacío
- WHEN se invoca `AddCachingServices` y se construye el provider
- THEN `IDistributedCache` MUST resolver a la implementación de StackExchangeRedis
- AND `InstanceName` MUST aplicarse como prefijo de claves

#### Scenario: registro idempotente

- GIVEN una `ServiceCollection`
- WHEN `AddCachingServices` se invoca y luego `AddInfrastructureServices` corre
- THEN MUST existir una sola registración efectiva de `IDistributedCache`

### Requirement: NuGet Redis dormido sin conexión por defecto

El sistema MUST referenciar `Microsoft.Extensions.Caching.StackExchangeRedis` (MIT) de forma
**dormida**: la referencia NuGet MUST NOT provocar ninguna conexión a Redis salvo cuando
`Provider = Redis`. Con el default `InMemory`, NO se MUST intentar resolución de host ni socket
hacia Redis.

#### Scenario: default no intenta conectar a Redis

- GIVEN el default `Provider = InMemory`
- WHEN la app arranca y resuelve `IDistributedCache`
- THEN MUST NOT abrirse ninguna conexión a Redis (cero I/O de red hacia Redis)

### Requirement: compatibilidad hacia atrás (sin consumidor)

El sistema MUST preservar el comportamiento existente de todos los endpoints. Este cambio MUST
NOT cablear ningún consumidor de cache (p.ej. `GetByKeyHashAsync` de api-key permanece sin
cache). La firma y respuestas de los endpoints actuales MUST permanecer inalteradas.

#### Scenario: endpoints sin cambio de comportamiento

- GIVEN la app con el seam de cache instalado y default `InMemory`
- WHEN se ejercitan los endpoints existentes (document/certificate/api-key/lifecycle/bootstrap)
- THEN su comportamiento observable MUST ser idéntico al previo al cambio

## Non-goals (fuera de alcance, declarado explícito)

Las siguientes capacidades MUST NOT formar parte de este cambio:

- **Rate limiter distribuido** — change de seguimiento separado; interim: limiting a nivel
  gateway (Traefik/NGINX).
- **Cualquier consumidor de cache** — la abstracción aterriza sola (D3).
- **Secuencial fiscal** — permanece en Postgres `FOR UPDATE`.
- **Idempotency store** — ya respaldado en BD; sin cambio.
- **Outbox/cola SRI asíncrona** — change futuro separado.
- Coordinación de background services — ya cross-pod safe vía `SKIP LOCKED`.
