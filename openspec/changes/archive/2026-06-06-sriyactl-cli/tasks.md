# Tasks: `sriyactl` — CLI day-2 ops (v1)

> Build/test: `go build ./...` · `go test ./...` (config.yaml). Repo hermano `sriyactl/`. Prose ES, código/identificadores EN.
> AI-friendly transversal = ai-contract spec. **Implementar UNA vez en render/middleware; NUNCA por comando** (riesgo señalado por el spec agent).

## Phase 1: Foundation (handler↔render cornerstone) — todo depende de esto

- [x] 1.1 `go mod init` (Go 1.23+) + layout de design.md: `cmd/sriyactl/`, `internal/{cli,core,api,compose,config,secret,render,errs}/`.
- [x] 1.2 `internal/core/output.go`: `Output[T]{SchemaVersion,Kind,Data}`, `Handler[In,Out]` (design Cornerstone). schemaVersion="1.0".
- [x] 1.3 `internal/errs/`: `CLIError{code,message,hint,retryable}` + map code→exit (0/1/2/3/4/5/6/7) — ai-contract *exit codes deterministas* + *errores como JSON*.
- [x] 1.4 `internal/render/render.go`: `Format(Table|JSON|YAML)`, `Renderable{Columns,Rows}`, `Render[T](w,out,f)`; error→JSON `{code,...}` cuando JSON/YAML — ai-contract *salida estructurada* + *errores JSON*.
- [x] 1.5 `internal/render/tty.go`: auto-non-TTY via `x/term.IsTerminal` → TTY=table, else json; `--output` explícito gana — ai-contract *modo no-TTY automático*.
- [x] 1.6 `internal/cli/middleware.go`: gate único `core.guardMutation` (`SRIYACTL_READONLY=1`/ctx read-only → exit 7) + plumbing `--dry-run`(retorna `Plan`)/`--yes`/`--no-input` — ai-contract *read-only bloquea* + *dry-run no-interactivo*. **AI-friendly transversal: una sola implementación.**
- [x] 1.7 `cmd/sriyactl/main.go` + `internal/cli/root.go`: cobra root, persistent flags `--output/--dir/--tenant/--yes/--no-input/--dry-run`, `--help`.

## Phase 2: Config & Secret layer

- [x] 2.1 `internal/config/`: viper + TOML `~/.config/sriyactl/config.toml` (solo non-secrets): `current_context/current_tenant`, `[contexts.*]`, `[tenants.*]` (design Config Model).
- [x] 2.2 `internal/config/context.go`: contextos kubectl-style + precedencia `flag>env>keychain>config`; `--tenant` ad-hoc no persiste — tenant *tenant use* (override).
- [x] 2.3 `internal/secret/store.go`: `Store{Get,Set}` con go-keyring; claves `sriyactl/<ctx>`, `sriyactl/<ctx>/<tenant>`; env fallback `SRIYACTL_SERVICE_TOKEN`/`SRIYACTL_API_KEY` (design Secret Model).

## Phase 3: API client + auth dispatch

- [x] 3.1 `internal/api/client.go`: interface `Client{Health,BootstrapTenant,CertStatus}` + impl `net/http` (design Interfaces — `ListTenants` removed per OQ 3.3; lives in `internal/config`).
- [x] 3.2 `internal/api/auth.go`: RoundTripper único — `ServiceToken`→`X-Service-Token`(+`X-Tenant-Id` scoped per-call), `TenantAPIKey`→`X-API-Key`; resuelve por precedencia (design Auth Dispatch). **Per-call `TenantID`**: el RoundTripper acepta un `TenantID` por invocación e inyecta `X-Tenant-Id` para ops scoped por tenant (cert, tenant get-by-id, lifecycle, admin). Omitido para `bootstrap` (no existe el tenant aún) y `health` (anónimo). Resuelto por cada handler desde el contexto activo o el override `--tenant <alias>`.
- [x] 3.3 **OPEN QUESTION (a)** — RESOLVED. Paths: `POST /api/v1/bootstrap` (multipart), `GET /health` (anonymous), `GET /api/v1/certificates` (X-Tenant-Id required with service token). No `GET /api/v1/tenants` (list) — `tenant list` is a local config read. See `apply-progress.md` OQ 3.3.
- [x] 3.4 **OPEN QUESTION (b)** — RESOLVED. Form is `multipart/form-data` with `ruc, razonSocial, ownerName, password, certificate` (file) + optional `nombreComercial, correoContacto, apiKeyName`. Header is `X-Service-Token` ONLY (no X-Tenant-Id). 201 returns `BootstrapTenantResponse` with `apiKey` (auto-captured to keychain). 400 → `tenant_duplicate` heuristic. See `apply-progress.md` OQ 3.4.

## Phase 4: Compose wrapper

- [x] 4.1 `internal/compose/runner.go`: interface `Runner{Run,Stream}` via `os/exec`; descubre install dir (`.env`+`docker-compose.yml`, override `--dir`/`SRIYACTL_HOME`); falta cualquiera → `install_dir_invalid` (infra Purpose).

## Phase 5: Commands (handler tipado + cli thin)

- [x] 5.1 `infra status`: agrega `compose ps`+`/health`+`/health/ready`+`BILLING_IMAGE_TAG`; degraded→exit≠0 — infra *infra status*.
- [x] 5.2 `infra logs [-f]`: `Stream` directo hasta SIGINT — infra *infra logs*.
- [x] 5.3 `infra upgrade --to`: migration-aware (backup→bump tag→pull→up→wait `/health/ready`); timeout→rollback tag+`upgrade_health_timeout` — infra *infra upgrade*. Mutador (guard 1.6).
- [x] 5.4 `infra backup`: `pg_dump` via `compose exec`; reporta `path`+`sizeBytes`; sin pg→`db_unavailable` — infra *infra backup*.
- [x] 5.5 `infra restore <file>`: destructivo (confirma/`--yes`/`--dry-run`) — infra *infra restore*. Usa guard+dry-run de 1.6 (no duplicar).
- [x] 5.6 `infra doctor`: preflight (docker/daemon/puerto/.env keys/imagen GHCR/`ENCRYPTION_KEY`≥32); fail→hint+exit≠0 — infra *infra doctor*.
- [x] 5.7 `tenant create`: `POST bootstrap`+`X-Service-Token`; auto-captura apiKey→keychain, alias en config; NO stdout salvo `--show`; dup→`tenant_duplicate` sin escribir — tenant *tenant create*. Mutador.
- [x] 5.8 `tenant list`: tenants del ctx (alias+id), marca activo — tenant *tenant list*.
- [x] 5.9 `tenant use <alias>` / `tenant current`: fija/persiste activo; alias inexistente→`tenant_not_found` — tenant *tenant use* + *tenant current*.
- [x] 5.10 `cert status <tenant> --warn-days N`: por cert exp+estado(valid/expiring/expired); expiring/expired→exit≠0 (señal CI); sin cert→`cert_not_found` — cert *cert status*.

## Phase 6: Release wiring

- [x] 6.1 `.goreleaser.yaml`: matriz mac/linux/windows × arm64/amd64, checksums, brew tap (proposal Success Criteria).
- [x] 6.2 Verificar cross-compile: `goreleaser release --snapshot --clean` (config in place; not run in this batch — `goreleaser` binary not present on this host. Smoke test of `go build` for darwin/arm64 passes via `GOOS=darwin GOARCH=arm64 go build`; full matrix requires CI).

## Phase 7: Docs

- [x] 7.1 `README.md` de `sriyactl`: install, contextos, comandos v1, `--output json`, exit codes, `SRIYACTL_READONLY`.
- [x] 7.2 `AGENTS.md` stub: contrato AI-friendly (json envelope+schemaVersion, exit codes, readonly, dry-run) para consumo por agentes.

## Phase 8: Testing (por capa — design Testing Strategy)

- [x] 8.1 Unit handlers `core`: mock `api.Client`/`compose.Runner`/`secret.Store`; cubrir escenarios de cada spec (sin red/Docker). See `internal/core/tenant_test.go`, `internal/core/cert_test.go`, `internal/core/guard_test.go`.
  - W-1 closed: infra_test.go added with 10 tests (status healthy/degraded, logs args, upgrade success+timeout-rollback, backup success+postgres-down, restore dry-run, doctor pass+enc-key-fail). Three 1-line production refactors in `infra_helpers.go`/`infra.go` (`lookPath`/`restoreViaStdin` from func→var, `5*time.Second`→`upgradeHealthPollInterval` var) to make the previously-untestable seams mockable.
- [x] 8.2 Golden-file render: por `kind` (TenantList at v1) en json/yaml; semantically equal via `json.Unmarshal` round-trip (table rendering added as new kinds land in v2). See `internal/render/golden_test.go`.
- [x] 8.3 Integration: wiring cli→core→render + precedencia auth con `httptest` fake. See `internal/cli/integration_test.go` (3 tests covering tenant list end-to-end, auth precedence, read-only blocks tenant create).
- [x] 8.4 ai-contract: tests de non-TTY→json, exit-code map, readonly→exit7, dry-run sin efectos, error JSON `{code,...}` (validar UNA vez en la capa transversal). See `internal/render/aicontract_test.go` + `internal/core/guard_test.go` + `internal/errs/errors_test.go` + `internal/cli/integration_test.go` (read-only case).

## Backend follow-up (out of CLI scope — track only, do not implement in this change)

- [ ] 8.6 Add stable `code` field to backend error response envelope
      - File: `src/Qora.Billing.Api/Middleware/GlobalExceptionHandler.cs`
      - Add a `code: string` property to the error envelope (e.g. `tenant_duplicate`, `cert_invalid`, `password_mismatch`).
      - Update `BillingDomainException` subclasses to set codes.
      - Unblocks CLI v2 to drop the string-heuristic mapping in `internal/api/errors.go`.

## Definition of Done

- [x] `go build ./...` compila sin error.
- [x] `go test ./...` pasa (unit + golden + integration).
- [x] `sriyactl --help` funciona y lista comandos v1.
- [x] `--output json` produce envelope válido con `schemaVersion` (validado).
- [x] Errores emiten `{code,message,hint,retryable}` + exit code estable.
- [x] `goreleaser release --snapshot --clean` config in place; cross-compile validation runs in CI (task 6.2 above).
- [x] Open questions (a) y (b) resueltas contra el código real del backend.

## Deferred to v2/v3 (NO scope-creep — fuera de este task list)

- **v2**: `sriyactl mcp` (MCP server), `spec --json`, `doc*` (send/list/show/status/void/events/ride), `apikey*`, `cert upload`, `tenant update/usage`, auth `TenantAPIKey` para documentos.
- **v3**: generadores `agents-md`/`skill --claude`, `sri ping`, `secrets rotate encryption-key`, JSONL en listas, `--field` selection, pulido de contextos.
