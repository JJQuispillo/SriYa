# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Emisión atómica con pre-reserva y reconciliador (PR #2 de `sri-emision-atomicidad`)**:
  - Nuevo flujo de emisión con 5 checkpoints persistentes en
    `ProcessDocumentCommandHandler`: C1 pre-reserva (lock `SELECT MAX(secuencial)
    ... FOR UPDATE` + INSERT Draft con dedupe ante identidad duplicada y chequeo
    de monotonicidad), C2 XmlGenerated, C3 Signed, C4 SentToSri (persistencia
    temprana — cierra la pérdida de comprobantes entre el envío al SRI y el
    guardado final), C5 Authorized/Rejected/PendingRetry. Las fallas transitorias
    del SRI dejan el documento en su último checkpoint persistido y el
    reconciliador lo rescata.
  - Nuevo `SriReconciliationService` (BackgroundService incondicional): barre
    documentos `SentToSri` varados (`FOR UPDATE SKIP LOCKED`, coordinación
    cross-pod sin leader election) y re-consulta su autorización.
  - `SriRetryService`: re-verificación del certificado en cada reintento
    (marca `Failed` si venció/inactivo, sin reenviar).
  - Nuevos POCOs `EmissionOptions` (`Sri:Emission`, flag `AtomicityEnabled`
    default true; `AutoGenerateSecuencial` default false) y
    `SriReconciliationOptions` (`Sri:Reconciliation`). Para detener el
    reconciliador: `Sri:Reconciliation:SweepIntervalSeconds=86400`.
  - `SriExceptionClassifier.IsSriTransientOrCircuitOpen` como fuente única de
    verdad de "¿es reintentable?"; `EmissionEvents` (EventIds 2001-2023) para
    observabilidad. Nuevos métodos de repositorio
    `GetMaxSecuencialWithLockAsync` y `GetStaleSentToSriAsync`.
  - Feature flag de rollback: `Sri:Emission:AtomicityEnabled=false` vuelve al
    flujo legacy de un solo checkpoint; el reconciliador sigue activo (D9.a).
- **Migration B6 (operational, additive) — `sri-emision-atomicidad` change**:
  2 nuevos índices parciales sobre la tabla `documents`, sin modificar
  columnas, constraints ni datos:
  - `ix_documents_tenant_secuencial`: compuesto
    `(tenant_id, document_type, estab, pto_emision, secuencial DESC)` con
    filtro `WHERE estab/pto_emision/secuencial IS NOT NULL`. Respalda el
    lock pesimista `SELECT MAX(secuencial) ... FOR UPDATE` de
    `IDocumentRepository.GetMaxSecuencialWithLockAsync` (pre-reservation).
  - `ix_documents_senttosri_createdat`: parcial sobre `(created_at)` con
    filtro `WHERE status = 'SentToSri'`. Respalda el sweep del
    reconciliador `SriReconciliationService` (búsqueda de documentos
    varados para rescate).
  Aplicada con `CREATE INDEX CONCURRENTLY` para cero downtime (NO toma
  `ACCESS EXCLUSIVE` lock). **IMPORTANTE**: debe aplicarse de forma
  operacional ANTES de desplegar el código de runtime de PR #2 (ventana
  separada, típicamente 1h antes del canary). Ver
  `docs/runbooks/sri-emision-atomicity.md` y el script
  `scripts/migrations/apply-B6-EmissionAtomicity.sh`.
  Migration file:
  `src/Qora.Billing.Infrastructure/Migrations/20260604100000_B6_EmissionAtomicity.cs`.
- **Sri resilience externalization** (change `sri-resiliencia-configuracion`):
  externalized SRI HTTP resilience parameters (timeout, retries, backoff,
  circuit breaker) from hardcoded literals into `appsettings.json` under
  `Sri:*` and `Sri:Retry:*`. All defaults match the previous hardcoded
  values (backward-compat guaranteed by snapshot tests).
- New domain exception `SriCircuitOpenException` translated from
  `Polly.CircuitBreaker.BrokenCircuitException` /
  `IsolatedCircuitException` at the Infrastructure boundary (Application
  layer no longer imports Polly).
- HTTP 503 mapping for `SriCircuitOpenException` with
  `ProblemDetails.Extensions["retryAfterSeconds"]` and
  `Extensions["reason"]`; logged at `Warning` level (not `Error`) to
  avoid contaminating 5xx alerts.
- Structured logging for circuit state transitions and retries via stable
  `EventId`s 1001-1003 (circuit) and 1010 (retry).
- `SriConfigurationValidator` (`IValidateOptions<SriConfiguration>`) that
  fails-fast at startup if `BreakDurationSeconds >= SamplingDurationSeconds`.
- Kill switch: setting `Sri:ResilienceEnabled=false` in `appsettings.json`
  makes the Polly pipeline a no-op pass-through (rollback without redeploy).

## [1.0.0] - 2026-06-02

### Added
- One-line self-host installer (`install.sh`) that generates secrets and starts
  the stack via Docker Compose.
- `docker-compose.prod.yml` that runs the pre-built GHCR image (no source
  checkout or .NET SDK required).
- Release workflow publishing multi-arch (amd64/arm64) images to
  `ghcr.io/jjquispillo/sriya` on `v*.*.*` tags.
- `ENCRYPTION_KEY` wired through `.env.example` and Docker Compose.
- Electronic document endpoints for the six SRI types (Factura, Liquidación de
  Compra, Nota de Crédito, Nota de Débito, Guía de Remisión, Retención),
  XAdES-BES signing, SRI submission, RIDE generation, and per-tenant
  certificate/API-key management.

### Fixed
- CI workflow no longer assumes a `billing/` monorepo subdirectory.
- Docker image labels now use the `SriYa API` title.

[Unreleased]: https://github.com/JJQuispillo/SriYa/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/JJQuispillo/SriYa/releases/tag/v1.0.0
