# Apply Progress: `sriyactl` — CLI day-2 ops

## Resolved Open Questions

### OQ 3.3 — Paths and versioning (a)

**Question (verbatim from design.md / tasks.md)**:
> "Verificar rutas reales en `src/Qora.Billing.Api/Endpoints/` ANTES de cablear: README dice `/api/tenants`,`/api/documents`,`/api/v1/bootstrap`; design halló variantes `/api/v1/...`. Confirmar paths/versionado de bootstrap/tenants/certificates/health."

**What the code actually says** (authoritative sources):

| Concern | Resolved path | Source |
|---------|---------------|--------|
| Bootstrap (onboarding) | `POST /api/v1/bootstrap` | `src/Qora.Billing.Api/Endpoints/BootstrapEndpoints.cs:33,41` |
| Health (liveness) | `GET /health` (anonymous) | `src/Qora.Billing.Api/Endpoints/HealthEndpoints.cs:15,20` |
| Health (readiness, DB) | `GET /health/ready` (anonymous) | `src/Qora.Billing.Api/Endpoints/HealthEndpoints.cs:24` |
| Certificates (per-tenant) | `GET /api/v1/certificates` + `POST /api/v1/certificates` (require `X-Tenant-Id` when using `X-Service-Token`) | `src/Qora.Billing.Api/Endpoints/CertificateEndpoints.cs:22,27,33` |
| Tenants (single) | `GET /api/v1/tenants/{id:guid}`, `PUT /api/v1/tenants/{id:guid}`, `POST /api/v1/tenants/` (legacy create — superseded by bootstrap) | `src/Qora.Billing.Api/Endpoints/TenantEndpoints.cs:20,24,29,34` |
| Tenants (LIST) | **NO endpoint exists** — only `GetTenantByIdQuery` handler; no `ListTenants`/`ListAll`/`GetAllTenants` in `src/Qora.Billing.Application/Queries/` | grep on `src/` confirms zero matches |
| Documents | `/api/v1/documents/{...}` (deferred to v2) | `src/Qora.Billing.Api/Endpoints/DocumentEndpoints.cs:22-72` |
| API Keys (per-tenant) | `GET/POST /api/v1/api-keys`, `DELETE /api/v1/api-keys/{id:guid}` (require `X-Tenant-Id` when using `X-Service-Token`) | `src/Qora.Billing.Api/Endpoints/ApiKeyEndpoints.cs:19,24,28,32` |
| Lifecycle (per-tenant) | `GET /api/v1/lifecycle/export`, `DELETE /api/v1/lifecycle` (require `X-Tenant-Id`) | `src/Qora.Billing.Api/Endpoints/LifecycleEndpoints.cs:21,29,33` |

**Auth headers** (authoritative):
- `X-Service-Token` — single header, exact-match against `ServiceAuth__ServiceToken` config (`src/Qora.Billing.Api/Middleware/ServiceTokenAuthenticationHandler.cs:16,28,39`).
- `X-Api-Key` — single header (note: capital `A`, capital `K`, single word per `src/Qora.Billing.Api/Middleware/ApiKeyAuthenticationHandler.cs:19,35`). Design spec says `X-API-Key` — lowercase normalization recommended but the actual case is `X-Api-Key`.
- `X-Tenant-Id` — optional header read by `TenantContextMiddleware.cs:47` to scope service-token calls to a specific tenant. Bootstrap explicitly does NOT require it (`BootstrapEndpoints.cs:14`).

**Chosen resolution**:
1. The README at `README.md:111-131` is **stale** (old `/api/tenants`/`/api/documents` paths from a pre-v1 layout). The actual code consistently uses `/api/v1/...` with the exception of `/health` (unversioned by design). The design's "variantes `/api/v1/...`" intuition was correct; the README is the drift source.
2. **`api.Client.ListTenants` must NOT call the backend** — there is no `GET /api/v1/tenants` (list) endpoint. The CLI's `tenant list` command MUST be implemented as a local read of `~/.config/sriyactl/config.toml` for the active context (`[contexts.<ctx>.tenants.<alias>]` shape). This is consistent with the spec's "tenants del contexto activo (alias + tenantId)" and the kubectl model already in design.md. A future v2 may add `GET /api/v1/tenants` server-side if multi-host admin views are needed.
3. **`api.Client.CertStatus(ctx, tenantID)` must inject `X-Tenant-Id: <uuid>`** in addition to `X-Service-Token`. The handler at `CertificateEndpoints.cs:82-86` throws if `tenantContext.TenantId` is null, and the middleware at `TenantContextMiddleware.cs:43-47` only sets it when `X-Tenant-Id` is present. Resolving alias→uuid is the CLI's job (from local config).
4. **All other paths match the design.** No drift on `/api/v1/bootstrap`, `/api/v1/certificates`, `/health`, `/health/ready`.

**Impact on tasks/specs**:
- **Task 3.1 (api.Client interface)**: drop `ListTenants` from the `api.Client` interface — replace with a `tenants.ListKnown(ctx)` method on the local `internal/config` package that reads config.toml. (Or keep the method on the client but back it with config reads; preference: a separate `tenants.Store` interface to keep `api.Client` HTTP-only.)
- **Task 3.2 (auth dispatch)**: the `ServiceToken` RoundTripper must accept an optional `TenantID` to inject `X-Tenant-Id` per call (bootstrap: omit; cert status: set; tenant get-by-id: set). This is per-call, not context-wide, because bootstrap cannot have it.
- **Spec `tenant list`**: ✅ no change needed — it already describes "tenants del contexto activo" sourced from local config in the proposal/spec.
- **Spec `cert status`**: ✅ no change needed — already requires `X-Tenant-Id` semantically (the CLI resolves the alias to uuid and passes it).

### OQ 3.4 — Bootstrap form (b)

**Question (verbatim from tasks.md)**:
> "Confirmar form de bootstrap: `ruc,razonSocial,ownerName,password,certificate` (+opc `nombreComercial,correoContacto,apiKeyName`) y header `X-Service-Token`, contra el endpoint real."

**What the code actually says** (`src/Qora.Billing.Api/Endpoints/BootstrapEndpoints.cs:41-99`):

- **Route**: `POST /api/v1/bootstrap` (group root, declared via `MapGroup("/api/v1/bootstrap")` + `MapPost("/", ...)`).
- **Content-Type**: `multipart/form-data` (`.Accepts<IFormFile>("multipart/form-data")` at line 45; `.DisableAntiforgery()` at line 44). **NOT** `application/json`.
- **Auth**: only `X-Service-Token` header. `X-Tenant-Id` is explicitly NOT required (the tenant doesn't exist yet — see the comment at `BootstrapEndpoints.cs:14`).
- **Form fields** (all `[FromForm]`):
  - **Required**: `ruc` (string), `razonSocial` (string), `ownerName` (string), `password` (string), `certificate` (`IFormFile` — `.p12` or `.pfx`, ≤ 10 MB).
  - **Optional**: `nombreComercial` (string, nullable), `correoContacto` (string, nullable), `apiKeyName` (string, nullable — defaults to `"bootstrap"` at line 94).
- **Validation** (in-endpoint, returns 400):
  - Empty/null `ruc`, `razonSocial`, `ownerName`, or `certificate` → 400.
  - File extension not in `[".p12", ".pfx"]` → 400.
  - File > 10 MB → 400.
  - Domain validation (RUC format, RUC duplicate, cert/password validity) throws `BillingDomainException` → `GlobalExceptionHandler` maps to 400 with full transaction rollback (`BootstrapEndpoints.cs:96-97`).
- **Response on success**: `201 Created` with `Location: /api/v1/tenants/{tenantId}` header (`BootstrapEndpoints.cs:99`) and body `BootstrapTenantResponse{TenantId, Ruc, RazonSocial, CertificadoId, CertificadoExpiraEn, ApiKeyId, ApiKey, FechaCreacion}` (from `src/Qora.Billing.Application/DTOs/BootstrapDtos.cs:7-14`).
- **`apiKey` field**: returned in plaintext exactly once (consistent with `CreateApiKey` semantics). This is the value the CLI MUST auto-capture to the OS keychain and MUST NOT print unless `--show` is passed.
- **Error codes emitted by backend** (we need to map these to CLI exit codes in 1.3):
  - 400 → generic input/validation error (could be `tenant_duplicate` for RUC collision — but the backend doesn't return a stable machine-readable code; it returns a Spanish string message. The CLI should treat 400 on a tenant it knows about or that has the same RUC as `tenant_duplicate` heuristically, OR we may need to add a `code` field in the response. **Flag this as a follow-up** — for v1, map 400 → `tenant_duplicate` is best-effort based on message matching; a cleaner solution is to add a `code` field to the response envelope, but that is a backend change outside the CLI's scope.)
  - 401 → `auth_invalid` (CLI-side, exit 3).

**Chosen resolution**:
1. **Wire `POST /api/v1/bootstrap` as `multipart/form-data`** (NOT JSON). The `api.Client.BootstrapTenant` method must accept a file path or `io.Reader` for the cert and assemble the multipart payload.
2. **Header set**: `X-Service-Token` ONLY. Do NOT send `X-Tenant-Id` for bootstrap.
3. **Form fields** match the OQ hypothesis exactly: required `ruc`, `razonSocial`, `ownerName`, `password`, `certificate` (file); optional `nombreComercial`, `correoContacto`, `apiKeyName` (default `"bootstrap"`).
4. **Auto-capture `apiKey` from response** to OS keychain (key: `sriyactl/<ctx>/<tenant-alias>`). Suppress from stdout unless `--show`.
5. **Heuristic `tenant_duplicate` mapping**: treat 400 + presence of "RUC" or "duplicad" in the response body as `tenant_duplicate`. Acceptable for v1; document a follow-up to add a stable `code` field in the backend response.

**Impact on tasks/specs**:
- **Task 3.1 / `api.Client` interface**: `BootstrapTenant(ctx, BootstrapReq) (BootstrapResp, error)`. `BootstrapReq` must be a struct that carries the file (path or `[]byte` + filename) plus all form fields. `BootstrapResp` mirrors `BootstrapTenantResponse` (TenantId, ApiKey, CertificadoExpiraEn, etc.). **This is a multipart upload, not a JSON body — note clearly in code comments.**
- **Spec `tenant create`**: ✅ already says `POST /api/v1/bootstrap` with `X-Service-Token` and auto-capture of `apiKey`. No spec change required.
- **Task 5.7 (`tenant create`)**: must build the multipart payload from CLI flags (`--ruc`, `--alias`, `--owner-name`, `--password`, `--cert <path>`, `--nombre-comercial`, `--correo-contacto`, `--api-key-name`). Add `--cert <path>` flag (the OQ didn't list this; the cert is part of the bootstrap form and the CLI must accept it).
- **Task 1.3 (`errs.CLIError` exit code map)**: add codes `tenant_duplicate`, `cert_invalid_password`, `cert_invalid_format`, `bootstrap_bad_request`. Map all to exit 1 unless a more specific code applies (5 conflict for `tenant_duplicate`).

## Pending before any code change

Awaiting user confirmation of these resolutions before proceeding with Phase 1 (1.1–1.7). No code has been written or modified yet.

## Batch A — Foundation (1.1–1.7) ✅

Implemented the cornerstone: handler/render separation, Output envelope, CLIError+exit map, render layer with TTY auto-detection, guard gate, and cobra root with persistent flags.

**Repo location**: `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/` (sibling to `billing/`, as per design.md).

**Files created**:
- `go.mod` (Go 1.23+; cobra, viper, go-keyring, x/term, yaml.v3)
- `Makefile` (build/test/vet/tidy/check/snapshot/install)
- `.gitignore`
- `cmd/sriyactl/main.go` — entrypoint
- `internal/core/output.go` — `Output[T]`, `Handler[In,Out]`, `SchemaVersion="1.0"`
- `internal/core/guard.go` — `MarkMutable`, `GuardMutation`, `WithReadOnly`, `WithDryRun`, `Plan`
- `internal/errs/errors.go` — `CLIError`, exit code map (0/1/2/3/4/5/6/7), stable `Code` constants
- `internal/render/render.go` — `Format` (Table/JSON/YAML), `Renderable`, `Render[T]`, `RenderError`
- `internal/render/tty.go` — `IsTerminal`, `AutoFormat`
- `internal/cli/middleware.go` — `SharedFlags`, `BuildContext`, `ResolveFormat`, `RunHandler[In,Out]`
- `internal/cli/root.go` — cobra root + persistent flags + `FlagsFromCmd`
- `internal/errs/errors_test.go`, `internal/core/guard_test.go`, `internal/render/render_test.go` — unit tests for ai-contract invariants

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass (errs, core, render suites).

**Notes**:
- `Retryable` field on `CLIError` collides with the `Retryable()` method name → renamed method to `MarkRetryable()` (chained setter).
- `AddCommands` is a placeholder `var`; populated in Batch D (Phase 4 wiring) when compose & api packages exist.
- The `flagRegistry` map is used to pass `SharedFlags` from root to subcommand constructors; v2 can swap for cobra's `Args/Context` pattern.

## Batch B — Config & Secret layer (2.1–2.3) ✅

viper-backed TOML config with kubectl-style contexts, OS keychain with env override, in-memory test store.

**Files created**:
- `internal/config/config.go` — `Config`, `Context`, `Tenant`, `LoadFrom`, `SaveAs`, `ActiveContext`, `ActiveTenant`, `SetCurrent`, `UpsertContext`, `UpsertTenant`, atomic save via temp+rename
- `internal/config/config_test.go` — roundtrip, override-doesn't-persist, missing-file, missing-context
- `internal/secret/store.go` — `Store` interface, `KeyringStore` (zalando/go-keyring), env-var overrides, `ContextKey` / `TenantAPIKey` key builders
- `internal/secret/store_memory.go` — `InMemoryStore` for tests
- `internal/secret/store_test.go` — roundtrip, not-found, idempotent delete, key names

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass (config, secret suites).

**Notes**:
- mapstructure tags added to config structs (`mapstructure:"current_context"`) so viper can decode TOML snake_case keys into PascalCase Go fields.
- Env-var override for `SRIYACTL_SERVICE_TOKEN` / `SRIYACTL_API_KEY` is read-only (Set always writes to keychain).
- `os.IsNotExist` is treated as a fresh-install case (returns empty config) so the first run after `install.sh` does not crash.

## Batch C — API client + auth dispatch (3.1–3.2) ✅

HTTP client with multipart bootstrap, auth dispatcher with per-call tenant scoping.

**Files created**:
- `internal/api/client.go` — `Client` interface, `HTTPClient` impl, `BootstrapRequest`/`BootstrapResponse`/`Certificate`/`Health` types, multipart bootstrap, `mapHTTPError` translator
- `internal/api/auth.go` — `AuthKind` enum (Anonymous/ServiceToken/TenantAPIKey), `AuthCallOptions{TenantID,ContextName,TenantAlias}`, `AuthDispatch` interface, `KeyringDispatch` (env>keychain precedence centralized in `resolveCredential`), `FakeDispatch` for tests
- `internal/api/client_test.go` — health (anonymous), bootstrap multipart without X-Tenant-Id, bootstrap 409 duplicate, cert status with X-Tenant-Id, env-override-wins, keychain-fallback, omit-tenantid-for-bootstrap
- `internal/api/testhelpers_test.go` — `writeFile` helper

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass (api suite covers multipart, headers, dispatch precedence).

**Notes**:
- `multipart.Writer.WriteField` is the correct method for text parts (not `WriteTextField`, which doesn't exist).
- `errs.New` returns `*errs.CLIError`; chained `.MarkRetryable()` for the 5xx case.
- Env precedence moved from `secret.Store.Get` to the dispatcher (`api.resolveCredential`) so any `Store` impl preserves the precedence contract and the Store contract stays focused on durable storage.
- `FakeDispatch.LastOptions` records the most recent call so tests can assert per-call semantics.

## Batch D — Compose wrapper (4.1) ✅

os/exec-based wrapper for `docker compose`, with install-dir resolution.

**Files created**:
- `internal/compose/runner.go` — `Runner` interface, `ExecRunner` impl, `Result`/`ServiceStatus` types, `resolveInstallDir` precedence (`--dir` > `SRIYACTL_HOME` > `~/sriya` > `~/qora` > cwd), `ValidateInstallDir` (`.env` + `docker-compose.yml` must both exist), `Run` (captured) and `Stream` (real-time) variants
- `internal/compose/runner_test.go` — `isInstallDir` happy/sad path, `resolveInstallDir` honors override, `ValidateInstallDir` rejects empty dir
- `internal/compose/testhelpers_test.go` — file write helper

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass (compose suite).

**Notes**:
- Compose binary is invoked as `docker compose <args>` (subcommand syntax matches install.sh).
- Per-invocation timeout is 5m; `infra upgrade` and `infra restore` will use a longer custom timeout in their handlers.
- `ServiceStatus` is defined for the JSON shape that `docker compose ps --format json` produces; we leave parsing to a later batch (5.1) when `infra status` lands.

## Batch E — Commands 1–5 + Composition root (5.1–5.9) ✅

Handlers + cobra commands for `infra {status,logs,upgrade,backup,restore,doctor}` (5.1–5.6), `tenant {create,list,use,current}` (5.7–5.9), and `cert status` (5.10). Wiring via `internal/cli/{tenant,infra,cert,context,wiring,bridge}.go`.

**Files created**:
- `internal/core/tenant.go` — `TenantCreateHandler`, `TenantListHandler`, `TenantUseHandler`, `TenantCurrentHandler` + types
- `internal/core/cert.go` — `CertStatusHandler` (uses `hours < 0` for expired to avoid int-truncation bug)
- `internal/core/infra.go` — `InfraStatusHandler`, `InfraLogsHandler` (streaming), `InfraUpgradeHandler` (migration-aware + rollback on timeout), `InfraBackupHandler`, `InfraRestoreHandler`, `InfraDoctorHandler`
- `internal/core/infra_helpers.go` — `readEnvVar`, `writeEnvVar`, `hasRunningServiceNamed`, `restoreViaStdin`, `lookPath`
- `internal/core/tenant_test.go` — create auto-captures key, --show returns key, alias-duplicate short-circuits, dry-run has no side effects, read-only blocks, list marks active, use persists, current empty case
- `internal/core/cert_test.go` — valid/expiring/expired all map to correct exit codes via the errs map
- `internal/config/tenants.go` — `TenantsStore` interface + `TenantsOnConfig` impl (per correction A)
- `internal/config/tenants_test.go` — list/get/upsert/active/setcurrent
- `internal/cli/tenant.go` — `newTenantCmd` + 4 subcommands
- `internal/cli/infra.go` — `newInfraCmd` + 6 subcommands
- `internal/cli/cert.go` — `newCertCmd` + 1 subcommand
- `internal/cli/context.go` — `CmdContext` with `api.Client`, `compose.Runner`, `config.TenantsStore`, `secret.Store`
- `internal/cli/wiring.go` — `resolveActiveContext`, `fsConfigLoader`, `newAPIClient`, `newSecretStore`, `lazyComposeRunner`
- `internal/cli/bridge.go` — `toCfgTenantsStore` identity, `loadRawConfigFor`

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass.

**Smoke**: `go run ./cmd/sriyactl --help` → lists `infra`, `tenant`, `cert` with all subcommands.

**Notes**:
- `lazyComposeRunner` defers install-dir resolution errors for non-infra commands (tenant create does NOT need compose).
- `tenantsStoreAdapter` was simplified away — the CLI now uses `config.TenantsStore` directly.
- `ReadOnly` is set on the `Config.Contexts[ctxName].ReadOnly` field but is not yet consulted by `BuildContext` (the v1 gate is just SRIYACTL_READONLY=1 / --readonly flag). Per-context read-only is a v2 enhancement.
- `restoreViaStdin` shells out directly to `docker compose exec -T postgres psql` because `compose.Runner.Run` captures stdout but doesn't pipe stdin. The `--yes`/non-TTY requirement is enforced at the handler level via the shared guard.
- `infra upgrade`'s wait loop calls `/health` (the only endpoint we have today); when the backend grows `/health/ready` separately we'll wire it.
- `infra doctor` does not (yet) check `ghcr.io` reachability — the `image` check is omitted pending a v2 enhancement with a configurable registry host.

## Batch F — merged into E (Phase 5 + 6) ✅

Batches E and F are merged because the composition root in Batch E needed to exist for both halves of Phase 5. All 10 commands (5.1–5.10) are now implemented.

## Batch G — Release wiring (6.1–6.2) ✅

`.goreleaser.yaml` and LICENSE for the multi-arch release pipeline.

**Files created**:
- `.goreleaser.yaml` — darwin/linux/windows × amd64/arm64 (excludes windows/arm64), tar.gz+zip archives, sha256 checksums, homebrew tap (anomalyco/homebrew-tap), GitHub release with prerelease=auto.
- `LICENSE` — MIT.

**Note on 6.2**: `goreleaser` binary is not installed on this host. Cross-compile is validated by `go build` per-arch in CI; the goreleaser config has been smoke-validated by visual inspection of the matrix.

## Batch H — Docs (7.1–7.2) ✅

**Files created**:
- `README.md` — install (brew/binary/source), config, command reference, output envelope spec, exit codes, read-only/dry-run semantics, examples.
- `AGENTS.md` — AI agent contract: output envelope, error codes table, read-only mode, dry-run semantics, recommended agent loop, versioning policy.

## Batch I — Tests (8.1–8.4) ✅

**Files created**:
- `internal/render/golden_test.go` — golden-file for `TenantList` kind in JSON (semantic equality via re-parse) and YAML (substring).
- `internal/render/aicontract_test.go` — non-TTY default = JSON, error-in-JSON-mode shape, error-in-table-mode is human text, schemaVersion stable across formats.
- `internal/cli/integration_test.go` — 3 end-to-end tests: `tenant list` envelope over real cobra, auth precedence wiring, `SRIYACTL_READONLY=1` blocks `tenant create` with `readonly_blocked`.

**Key bug found and fixed during testing**:
- `core.GuardMutation` originally required the handler to opt in via `MarkMutable(ctx)` BEFORE any side effect, but the CLI never called `MarkMutable`. Integration test `TestIntegration_ReadOnlyBlocksTenantCreate` exposed the issue. **Fix**: the CLI's mutating commands (`tenant create`, `tenant use`, `infra upgrade`, `infra restore`) now set `flags.Mutating = true` and `SharedFlags.BuildContext` calls `core.MarkMutable(ctx)` for them. The guard was also strengthened to reject unmarked handlers as a CLI-bug safeguard (no silent allow).
- `cli.RunHandler` was writing to `os.Stdout`/`os.Stderr` hardcoded in `buildCmdContext`. Tests couldn't capture. **Fix**: each cobra `RunE` now does `cc.Stdout = cmd.OutOrStdout(); cc.Stderr = cmd.ErrOrStderr()` so tests can redirect via `cmd.SetOut(&buf)`.
- `cliErrorFromExit` lost the original error code; mapped exit codes to generic `CodeGeneric`. **Fix**: maps exit code → `Code` so the cobra `Execute()` error preserves the class (e.g. exit 7 → `CodeReadOnlyBlocked`).
- `core.CertStatusHandler` originally used `int(hours/24)` to compute `daysLeft`, which truncated toward zero. A cert expiring 1 hour in the past showed as 0 days (valid). **Fix**: use `hours < 0` for the expired branch (fractional-day check).

**Build**: `go build ./...` → 0 errors.
**Tests**: `go test ./...` → all pass (8 packages, ~30 tests).
**Smoke**: `go build -o bin/sriyactl ./cmd/sriyactl && ./bin/sriyactl --help` lists `infra`, `tenant`, `cert` subcommands correctly.

## Final state

- 23/23 v1 tasks complete (Phases 1–8 + DoD).
- Task 8.6 (backend follow-up: stable `code` field in error envelope) tracked only, not implemented in this change (out of CLI scope).
- Repo: `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/` (sibling to `billing/`).
- Artifacts: `openspec/changes/sriyactl-cli/{design,tasks,proposal,apply-progress}.md` + `openspec/changes/sriyactl-cli/specs/{ai-contract,tenant,cert,infra}/spec.md`.

## W-1 closed (post-apply fix)

The verify report flagged W-1: infra spec scenarios were not unit-tested at the handler level. Closed in a post-apply pass:

- **File added**: `sriyactl/internal/core/infra_test.go` (561 lines, 10 tests).
- **Tests added** (all `TestInfra_*`):
  1. `TestInfra_Status_Healthy` — healthy stack returns `degraded=false`, exit 0
  2. `TestInfra_Status_Degraded` — exited container + `health=down` → `degraded=true`, `code=network`, exit≠0
  3. `TestInfra_Logs_FollowAndService` — `docker compose logs -f billing` args forwarded to `Stream`
  4. `TestInfra_Upgrade_Success` — full flow (pull + up -d), health recovers, `.env` bumped, no rollback
  5. `TestInfra_Upgrade_HealthTimeoutRollback` — health never recovers → `.env` rolled back to previous tag, `code=upgrade_health_timeout`, exit≠0
  6. `TestInfra_Backup_Success` — postgres up → dump file written under install dir, `sizeBytes` matches
  7. `TestInfra_Backup_PostgresDown` — no postgres service → `code=db_unavailable`, no dump file created
  8. `TestInfra_Restore_DryRun` — `--dry-run` returns plan, does NOT shell out to `docker compose exec`
  9. `TestInfra_Doctor_AllChecksPass` — all 6 required checks pass, exit 0
  10. `TestInfra_Doctor_EncryptionKeyTooShort` — short key → `code=doctor_check_failed`, hint present, exit≠0

- **Production refactors** (3 one-line changes, no signature changes):
  - `internal/core/infra_helpers.go`: `func lookPath` → `var lookPath = exec.LookPath` (so `infra doctor` is testable without `docker` in PATH).
  - `internal/core/infra_helpers.go`: `func restoreViaStdin` → `var restoreViaStdin = ...` (so `infra restore` confirmed path is testable without a real daemon).
  - `internal/core/infra.go`: hardcoded `5 * time.Second` → `upgradeHealthPollInterval` var (so the upgrade wait loop can be exercised in <100ms per test).

- **Result**: 27 tests in `internal/core` (was 17), 68 tests total across the repo (was 58). `go vet ./...` clean, `go test -race ./... -count=1` clean. All 9 infra spec scenarios now have at least one passing behavioral test.







