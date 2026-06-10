# Tasks: `sriyactl` instalador all-in-one + distribución Homebrew

Convenciones: `[CODE]` = puro código, `[USER]` = requiere acción del usuario o recurso externo (GitHub).
Dependencias entre fases marcadas con `(dep: …)`.

## Fase 1 — Foundation: rename anomalyco→JJQuispillo (F3)

- [x] T-INST-001 `[CODE]` sed `anomalyco/sriyactl`→`JJQuispillo/sriyactl` en los 29 `.go` + module path de `go.mod` (spec distribution: ownership).
- [x] T-INST-002 `[CODE]` Actualizar `.goreleaser.yaml`: `tap.owner`, `homepage`, `release.github.owner`=JJQuispillo; y README (F3).
- [x] T-INST-003 `[CODE]` Gate: `go build ./... && go test ./...` verde + `grep -r anomalyco` sin coincidencias (scenario "build verde").

## Fase 2 — Paquete `internal/installer` (dep: Fase 1)

- [x] T-INST-010 `[CODE]` `secret.go`: `GenSecret(n)` crypto/rand rejection-sampling `[A-Za-z0-9]`, n=44 (Decisión #2).
- [x] T-INST-011 `[CODE]` `env.go`: `RenderEnv(dir,EnvConfig)` 9 claves (incl. SERVICE_AUTH_TOKEN/BILLING_DB_PASSWORD), no-clobber, chmod 600.
- [x] T-INST-012 `[CODE]` `compose.go`: `DownloadCompose(dir,version)` raw GitHub `JJQuispillo/SriYa@v$VERSION`, no-clobber, no deja parcial en fallo.
- [x] T-INST-013 `[CODE]` `deps.go`: `DetectDocker()` + OS detect helpers.
- [x] T-INST-014 `[CODE]` `prompt.go`: prompts TTY (`x/term`) port/cors/db-user (+ bootstrap fields → diferido a Fase 5 T-INST-041, donde el handler conoce el contrato de bootstrap).

## Fase 3 — Handler + comando `infra install` (dep: Fase 2)

- [x] T-INST-020 `[CODE]` `core/infra.go`: `InfraDoctorHandler` gana `InfraDoctorRequest{PreInstall bool}` (pre=docker+daemon vía `installer.DetectDocker`/probe seam, sin install dir; post=set completo). `InfraDeps` gana `Probe installer.DockerProbe`. La firma cambió de `Handler[struct{},…]` a `Handler[InfraDoctorRequest,…]`; cli + 2 tests actualizados. NOTA: env-key sigue `POSTGRES_PASSWORD` (el fix a `BILLING_DB_PASSWORD` es Fase 6 T-INST-051, fuera de scope).
- [x] T-INST-021 `[CODE]` `core/infra_install.go`: `InfraInstallRequest`/`InfraInstallResult`/`InfraInstallDeps` + `InfraInstallHandler` (doctor pre→env→compose→pull/up -d→health-wait 90s `/health/ready`→next-step). Seams inyectables: `Fetcher`, `DockerProbe`, `ComposeRunnerFactory`, `ReadyProbe`. Nuevo error code `install_health_timeout` (exit 10).
- [x] T-INST-022 `[CODE]` `InfraInstallResult` implementa `render.Renderable` (`Columns()/Rows()`, excluye TenantID/APIKey); `--output json` vía el pipeline existente. (No se añadió progreso por Stderr: el handler permanece puro handler↔render; los pasos de progreso quedan como mejora opcional futura, no requerido por el contrato.)
- [x] T-INST-023 `[CODE]` `cli/infra.go`: `newInfraInstallCmd` con flags `--version/--port/--cors-origin/--db-user/--auto-install/--no-bootstrap/--timeout`; resuelve dir destino (`--dir`>`SRIYACTL_HOME`>`$HOME/sriya`) sin `buildCmdContext` (el dir aún no existe); compone installer+handler con seams de producción. Registrado en `newInfraCmd`.
- [x] T-INST-024 `[CODE]` Idempotencia: `RenderEnv`/`DownloadCompose` no-clobber (re-run reporta `envCreated=false`/`composeCreated=false`, reusa el tag existente del `.env`); `up -d` idempotente. Test `TestInfraInstall_IdempotentNoClobber` verifica que el secreto pre-existente no se rota.

## Fase 4 — `--auto-install` deps (dep: Fase 3)

- [ ] T-INST-030 `[CODE]` `deps.go`: `EnsureDocker(ctx,autoInstall)`: macOS `brew install colima docker docker-compose` + `colima start` si brew presente; Linux solo guía sin sudo (Decisión #4).

## Fase 5 — Encadenado bootstrap + seed contexto/token (F5) (dep: Fase 3)

- [ ] T-INST-040 `[CODE]` `cli/wiring.go`: helper que siembra contexto `local` en `config.toml` + service token en keychain con fallback env `SRIYACTL_SERVICE_TOKEN` (Decisión #7).
- [ ] T-INST-041 `[CODE]` Encadenar bootstrap: TTY interactivo por defecto, headless por flags (--ruc/--razon-social/--cert…), `--no-bootstrap` salta; usa `POST /api/v1/bootstrap` + `X-Service-Token`; falta flag headless → `code: bootstrap_input_required`.

## Fase 6 — Bug fixes stack real (dep: Fase 1)

- [ ] T-INST-050 `[CODE]` F1 `runBackup`: leer `BILLING_DB_USER` (default `billing_user`), DB `qora_billing`, servicio `billing-db` (spec infra backup, Decisión #3).
- [ ] T-INST-051 `[CODE]` F2 doctor: env-key `BILLING_DB_PASSWORD` (no `POSTGRES_PASSWORD`) (spec infra doctor).

## Fase 7 — Shim `install.sh` (dep: Fase 3)

- [ ] T-INST-060 `[CODE]` Reescribir `billing/install.sh`: detect OS/arch → brew o binario+verificación checksum → `exec sriyactl infra install`; rehúsa en OS/arch no soportado y aborta si checksum no coincide (spec distribution: shim).

## Fase 8 — Distribución (dep: Fase 1, Fase 7)

- [ ] T-INST-070 `[CODE]` Crear `.github/workflows/release.yml`: `goreleaser release --clean` on `push: tags: v*` (F4).
- [ ] T-INST-071 `[CODE]` `.goreleaser.yaml`: `release.draft:false`; Windows/arm64 `ignore` en matriz (spec release).
- [ ] T-INST-072 `[USER]` `git init` + crear y push público del repo `sriyactl` en GitHub.
- [ ] T-INST-073 `[USER]` Crear repo público `JJQuispillo/homebrew-tap`.
- [ ] T-INST-074 `[USER]` Crear PAT y registrarlo como secret `HOMEBREW_TAP_GITHUB_TOKEN`.
- [ ] T-INST-075 `[USER]` Push tag `v1.0.0` → verifica release publicado (no draft) + fórmula en tap + `brew install JJQuispillo/tap/sriyactl`.

## Fase 9 — Tests + verify gates

- [ ] T-INST-080 `[CODE]` Unit: GenSecret charset/len/entropía; RenderEnv no-clobber+chmod600; OS detect (tablas+tmpdir).
- [ ] T-INST-081 `[CODE]` Unit: runBackup args (billing-db/billing_user/qora_billing) + doctor env-key vía FakeRunner asserts.
- [ ] T-INST-082 `[CODE]` Integration InfraInstallHandler: happy / no-clobber / `install_health_timeout` / headless con FakeRunner+FakeAPI+InMemory secret, contra el contrato REAL (`POST /api/v1/bootstrap`, `/health/ready`) — NO un contrato fabricado.
- [ ] T-INST-083 `[USER]` E2E manual host limpio: shim instala binario + `infra install` deja stack healthy + tenant.

## Definition of Done

- `go build ./...` con 0 warnings y `go vet ./...` limpio.
- `go test ./...` verde; los tests de integración validan el contrato REAL del backend (no fabricado — el verify previo de sriyactl no era confiable por testear un contrato inexistente).
- `grep -r anomalyco` sin coincidencias.
- `--output json` emite errores con `code` determinista; exit 0 en éxito.
- Idempotencia: re-run de `infra install` no destruye secretos ni datos.
- Tareas `[USER]` (T-INST-072..075, T-INST-083) completadas o explícitamente diferidas con señal al usuario.
