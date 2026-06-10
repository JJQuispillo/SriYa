# Design: `sriyactl` instalador all-in-one + distribución Homebrew

## Technical Approach

Enfoque 2: paquete `internal/installer` (net-new: gen de secretos, render `.env`, descarga compose pin-a-tag, prompts TTY, detección/auto-install de deps) + un handler `core.InfraInstallHandler` que orquesta reusando `InfraDoctorHandler` (preflight), `ExecRunner` (pull/up), el polling de `Ready` y `BootstrapTenant`. Se preserva la separación handler↔render: `infra install` devuelve un `Output[InfraInstallResult]` tipado; el render layer (`render.Render`) decide tabla/json. El progreso por pasos se emite vía `Stderr` con `info/ok` (no contamina stdout estructurado), igual que el patrón streaming de `infra logs`.

## Architecture Decisions

| # | Decisión | Elegido | Alternativa | Rationale |
|---|----------|---------|-------------|-----------|
| 1 | Ubicación de la lógica nueva | `internal/installer` package | monolito en `core/infra.go` | Aísla I/O de red/fs (download, render, secretos) de lifecycle; testeable con fakes |
| 2 | Gen de secretos | `installer.GenSecret(n)` charset `[A-Za-z0-9]` desde `crypto/rand` rejection-sampling, n=44 | base64 | base64 mete `+/=` → rompe Npgsql + roles SQL (paridad `gen_secret`) |
| 3 | **Naming DB (OQ-a)** | **Leer de `.env`/compose**: `runBackup` lee `BILLING_DB_USER` (default `billing_user`), DB fija `qora_billing`, servicio `billing-db` | hardcode | El user es configurable en `.env`; hardcodear rompe instalaciones con `--db-user` custom |
| 4 | **`--auto-install` (OQ-b)** | macOS: `brew install colima docker docker-compose` **y** `colima start` (instalar sin arrancar deja `infra install` fallando acto seguido); Linux: sólo guiar | sólo instalar | Sin `colima start` el daemon no responde y el preflight post-install falla igual |
| 5 | Preflight split (F6) | `InfraDoctorHandler` gana modo `PreInstall bool`: pre = docker binary + daemon; post = + install-dir + env-keys | check único | En `install` el dir/env aún no existen |
| 6 | Versión / pin | `VERSION` = flag `--version`, fallback a `main.version` (ldflags), fallback `"latest"`/REF=main | sólo flag | El binario brew ya conoce su tag; pin compose a `JJQuispillo/SriYa@v$VERSION` |
| 7 | Seed contexto+token (F5) | `infra install` escribe contexto `local` en `config.toml` + `Set("service_token")` en keychain, fallback env `SRIYACTL_SERVICE_TOKEN` | manual | Resuelve el 2º chicken-and-egg para encadenar bootstrap |

## Data Flow

```
install.sh (shim)            sriyactl infra install
  detect OS/arch        ┌─> InfraInstallHandler (core)
  brew OR binary+sha →  │     1. doctor(PreInstall) ── installer.EnsureDocker(--auto-install)
  exec sriyactl install─┘     2. installer.DownloadCompose(VERSION)  [no-clobber]
                              3. installer.RenderEnv(cfg)            [no-clobber, chmod 600]
                              4. seedContext+keychain (config + secret.Store)
                              5. Compose.Run pull / up -d
                              6. poll API.Ready (90s)
                              7. (TTY|flags) → API.BootstrapTenant   else print next-step
                              └─> Output[InfraInstallResult] → render (table/json)
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `internal/installer/secret.go` | Create | `GenSecret(n)` crypto/rand alnum |
| `internal/installer/env.go` | Create | `RenderEnv(EnvConfig)` 9 claves, no-clobber, chmod 600 |
| `internal/installer/compose.go` | Create | `DownloadCompose(dir,version)` desde raw GitHub, no-clobber |
| `internal/installer/deps.go` | Create | `DetectDocker()`, `EnsureDocker(autoInstall)` (OS detect, brew/colima) |
| `internal/installer/prompt.go` | Create | prompts TTY (`x/term`) port/cors/db-user + bootstrap fields |
| `internal/core/infra.go` | Modify | `runBackup` lee user/db/servicio (F1); `InfraDoctorHandler` env-key `BILLING_DB_PASSWORD` + `PreInstall` (F2/F6); nuevo `InfraInstallHandler`+Result |
| `internal/cli/infra.go` | Modify | `newInfraInstallCmd` con flags; compone installer+handlers |
| `internal/cli/wiring.go` | Modify | helper seed contexto `local`+token (F5) |
| `internal/render/render.go` | Modify | `InfraInstallResult` implementa `Renderable` |
| `go.mod` + 29 archivos `.go` | Modify | rename `anomalyco`→`JJQuispillo` (F3) |
| `.goreleaser.yaml` | Modify | tap.owner/homepage/release.owner=JJQuispillo; `draft:false` (F3/F4) |
| `.github/workflows/release.yml` | Create | `goreleaser release --clean` en tags `v*` (F4) |
| `billing/install.sh` | Modify | shim: detect OS/arch → brew o binario+sha256 → `exec sriyactl infra install` |

## Interfaces / Contracts

```go
// internal/installer
func GenSecret(n int) (string, error) // [A-Za-z0-9] via crypto/rand
type EnvConfig struct { Version, Port, CorsOrigin, DBUser string } // 9 keys: BILLING_IMAGE_TAG, BILLING_API_PORT, BILLING_DB_USER, BILLING_DB_PASSWORD, BILLING_APP_DB_PASSWORD, BILLING_PRIVILEGED_DB_PASSWORD, SERVICE_AUTH_TOKEN, ENCRYPTION_KEY, CORS_ORIGIN_0
func RenderEnv(dir string, c EnvConfig) (created bool, err error)   // no-clobber
func DownloadCompose(dir, version string) (created bool, err error) // no-clobber
func EnsureDocker(ctx context.Context, autoInstall bool) error      // detect+guide / brew colima

// internal/core
type InfraInstallRequest struct { Version, Port, CorsOrigin, DBUser, Dir string; AutoInstall, NoBootstrap bool; Boot api.BootstrapRequest }
type InfraInstallResult struct { InstallDir, ImageTag string; EnvCreated, ComposeCreated bool; Healthy bool; TenantID, APIKey string `json:"-"`; NextStep string }
func InfraInstallHandler(d InfraDeps, inst installer.Service, sec secret.Store, cfg ConfigWriter) Handler[InfraInstallRequest, InfraInstallResult]
```

## Testing Strategy

| Layer | Qué | Cómo |
|-------|-----|------|
| Unit | GenSecret charset/len/entropía; RenderEnv no-clobber+chmod; OS detect | tablas + tmpdir |
| Unit | runBackup usa billing-db/billing_user/qora_billing; doctor env-key | FakeRunner asserts args |
| Integration | InfraInstallHandler happy/no-clobber/health-timeout/headless | FakeRunner+FakeAPI+InMemory secret |
| Build | `go build ./... && go test ./...` post-rename | CI |
| E2E | shim instala binario + `infra install` deja stack healthy+tenant | manual host limpio |

## Migration / Rollout

Rename seguro: `find -name '*.go' | xargs sed -i '' 's#anomalyco/sriyactl#JJQuispillo/sriyactl#g'` + sed en go.mod/.goreleaser, luego `go build ./... && go test ./...`. Distribución: `git init` + push público; crear `JJQuispillo/homebrew-tap`; PAT `HOMEBREW_TAP_GITHUB_TOKEN` como secret; workflow on `push: tags: v*`; flip `release.draft:false`; tag `v1.0.0` dispara fórmula. No-clobber garantiza idempotencia sobre instalaciones previas.

## Open Questions

- [x] OQ-a naming DB → leer de `.env` (Decisión #3).
- [x] OQ-b `--auto-install` → macOS instala **y** `colima start`; Linux sólo guía (Decisión #4).
