# Apply Progress: sriyactl-v1-fixes

> **Repo objetivo:** `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/`
> **Persistencia:** openspec (archivos en este folder)
> **Baseline:** `go build ./...` ✓ · `go vet ./...` ✓ · `go test ./... -count=1` ✓ (69 tests passing)
> **Final:** `go build ./...` ✓ · `go vet ./...` ✓ · `go test ./... -count=1` ✓ (88 tests passing, 0 fails) · `go test -race ./... -count=1` ✓ · `gofmt -l .` vacío

## Plan de batches

| Batch | Foco | Tareas | Riesgo |
|-------|------|--------|--------|
| A | `errs` (Renderable, exit codes) + `api/client.go` (DTO, Ready, 400+ProblemDetails) + `compose/runner.go` (RunTo) | A1, A2, A3 | medio (cambia contratos internos) |
| B | `cli/confirm.go` (nuevo) + `cli/middleware.go` (RunHandler render+sentinel + plug Confirm) + `cli/wiring.go` (RequiresConfirm) | B1, B2, B3, B4 | medio (cambia pipeline) |
| C | `core/cert.go` (status desde FechaExpiracion, empty list) + `core/infra.go` (Ready real, backup pre-upgrade, streaming) | C1, C2 | medio (cambia handlers) |
| D | Reescritura de tests al contrato real (cert/tenant/infra/api/errs) | D1, D2, D3, D4, D5 | alto (cambia fixtures) |
| E | goreleaser (ldflags + vars) + gofmt + verificación final | E1, E2, E3, E4 | bajo |

## Decisiones tomadas (open questions del design)

1. **Cert `Issuer`:** se ELIMINA de `CertStatusEntry` (recomendado en design §#2). Resuelto OQ.
2. **Backup pre-upgrade:** se crea SIEMPRE uno nuevo (recomendado en design §#7 OQ). Si falla, se aborta sin mutar `.env`. `runBackup(ctx, d)` se extrajo como helper compartido entre `InfraBackupHandler` y `InfraUpgradeHandler`.
3. **`internal/cli/runner.go` mencionado en tasks 4.2:** no existe como archivo; se interpreta como typo por `internal/compose/runner.go` (el modificado en A3). Resuelto: el gofmt de la Fase 4.2 cubre ese archivo.
4. **Comparación de status health:** `Status == "Healthy"` (liveness) y `Status == "Ready"` (readiness), PascalCase — no `"ok"`.
5. **Confirm gate TTY check:** se inyecta vía variable package-level `isTerminalFn` (overridable en tests) para evitar dependencia del TTY del host.
6. **Renderable flow:** `errors.As(err, &r)` check; si `r.Renderable()`, payload a stdout PRIMERO, luego error envelope a stderr, exit code derivado del error.
7. **Precedence gate:** `GuardMutation` (exit 7) → `Confirm` (exit 2 si non-TTY sin bypass) → handler. `--dry-run` skipea Confirm.
8. **Backup code preservation:** cuando `runBackup` falla dentro de `InfraUpgradeHandler`, se preserva el code original del CLIError (e.g. `db_unavailable` → exit 6) en vez de colapsarlo a `CodeGeneric` (exit 1).
9. **Build-time version vars:** se añaden `version`, `commit`, `date` en `cmd/sriyactl/main.go` con defaults `dev/none/unknown`; goreleaser las inyecta vía `ldflags:` (forma canónica, NO `-ldflags=-X` repetido).

## Batch log

### Batch A — errs + api + compose (DONE)
- `internal/errs/errors.go`:
  - Añadido `Renderable` interface + `RenderPayload bool` + `MarkRenderable() *CLIError` + `MarkRetryable() *CLIError`.
  - Nuevos codes: `CodeConfirmRequired`, `CodeConfirmAborted`, `CodeDoctorCheckFailed`, `CodeDBUnavailable`, `CodeDockerUnavailable` (algunos ya existían, se re-confirmaron).
  - `codeToExit` desambiguado: `cert_expiring→8`, `cert_expired→9`, `upgrade_health_timeout→10`, `doctor_check_failed→11`, `confirm_required/aborted→2`, `tenant_duplicate→5`. `CodeUpgradeTimeout` mantiene wire value `"upgrade_health_timeout"`.
- `internal/api/client.go`:
  - `Certificate` realineado a `ID, NombrePropietario, FechaExpiracion, Activo, FechaCreacion` con json tags camelCase.
  - `Health` sin `serviceTag`.
  - Nueva `Ready(ctx)` que pega `/health/ready`: 200 → `Health{Status:"Ready",...}`; 503 → `CodeDBUnavailable` retryable.
  - `BootstrapTenant`: rama 409 eliminada; nueva `classifyBootstrapError` que parsea ProblemDetails y mapea `"ya existe un tenant"` → `CodeTenantDuplicate` (exit 5); otros 400 → `CodeBootstrapBadReq` con `Detail` verbatim.
  - `CertStatus`: rama 404 eliminada; lista vacía 200 ya no es error.
- `internal/compose/runner.go`:
  - `Runner` interface: añadido `RunTo(ctx, w io.Writer, args ...string) error`.
  - `ExecRunner.RunTo`: `cmd.Stdout = w` (binary-safe streaming), `cmd.Stderr = &bytes.Buffer{}` (capturado para el error).
- `internal/cli/wiring.go`: `lazyComposeRunner` implementa `RunTo` (delega a `ExecRunner`).

### Batch B — confirm + middleware (DONE)
- NEW `internal/cli/confirm.go`:
  - `Confirm(flags, stdin, stdout, desc)` con TTY×bypass table: `--yes`/`--no-input` → `nil`; non-TTY sin bypass → `CodeConfirmRequired`; TTY interactivo → prompt `[y/N]`, sólo `y`/`yes` procede, resto (incl. EOF) → `CodeConfirmAborted`.
  - `isTerminalFn` es var package-level para tests.
- `internal/cli/middleware.go`:
  - `SharedFlags.RequiresConfirm bool` + `ConfirmDescription string` + `DryRun bool` (este último ya existía, se reusó).
  - `RunHandler`: invoca `Confirm` cuando `Mutating && RequiresConfirm && !DryRun` y antes de `handler(ctx, in)`.
  - Manejo de error rediseñado: si `errors.As(err, &r)` y `r.Renderable()` → `render.Render(stdout, out, format)` PRIMERO, luego `render.RenderError(stderr, err, format)`. Errores fatales (no-renderable) → solo stderr (back-compat).
- `internal/cli/infra.go`: `infra restore` y `infra upgrade` con `RequiresConfirm=true` + `ConfirmDescription` cableados.

### Batch C — core handlers (DONE)
- `internal/core/cert.go`:
  - `CertStatusHandler` deriva `Status` desde `FechaExpiracion + Activo + warn-days`, comparando en UTC.
  - `Subject` poblado desde `NombrePropietario`.
  - `Issuer` REMOVIDO de `CertStatusEntry`.
  - `Activo=false` → `expired`.
  - Lista vacía → `CodeCertNotFound` (exit 4).
  - Sentinels `expiring`/`expired` marcados `MarkRenderable()`.
- `internal/core/infra.go`:
  - `InfraStatusHandler` usa endpoints distintos: `/health` (liveness) y `/health/ready` (readiness).
  - Comparación PascalCase: `Status == "Healthy"` y `Status == "Ready"`.
  - `Degraded = (liveness≠Healthy) || (readiness≠Ready) || (servicio no corriendo)`.
  - Sentinel `degraded` es `Renderable`+`CodeNetwork`.
  - `InfraUpgradeHandler` orden mandatorio: `GuardMutation` → validate `--to` → **backup** (helper `runBackup` compartido) → write tag → `pull` → `up -d` → wait Ready.
  - Backup code preservation: si `runBackup` retorna `*CLIError`, upgrade propaga el code original; otros errores → `CodeGeneric`.
  - Nuevos: `InfraBackupHandler`, `InfraRestoreHandler`, `InfraDoctorHandler`.
  - `runBackup` usa `RunTo` (binary-safe streaming); en error mid-stream → `os.Remove(partial)` antes de propagar.
- `cmd/sriyactl/main.go`: añadidos `var version/commit/date` con defaults dev.

### Batch D — test rewrites (DONE)
- `internal/api/testhelpers_test.go`: doc comment añadido.
- `internal/api/client_test.go`:
  - Reescrito `TestHealth_OK` (asserts `"Healthy"`).
  - Añadidos: `TestReady_OK`, `TestReady_503_MapsToDBUnavailable`, `TestBootstrap_DuplicateIs400WithProblemDetails` (real HTTP stub, asserts `tenant_duplicate` desde 400+Spanish sentinel), `TestBootstrap_Other400IsBadRequest`, `TestCertStatus_EmptyList200`.
  - ELIMINADO `TestBootstrap_DuplicateIs409` (afirmaba código fabricado).
- `internal/core/tenant_test.go`:
  - `fakeAPI` con nuevo `Ready()`.
  - `TestTenantCreate_DuplicateIs400WithProblemDetails` (stub real, asserts NO escribe alias/keychain en duplicado).
  - `TestTenantCreate_Other400IsBadRequest`.
- `internal/core/cert_test.go`:
  - Reescrito con `fakeCertAPI` (incluye `Ready()`).
  - Tests: `TestCertStatus_Valid`, `TestCertStatus_ExpiringIsCIError` (exit 8, Renderable), `TestCertStatus_ExpiredIsCIError` (exit 9), `TestCertStatus_RevokedIsExpired`, `TestCertStatus_EmptyListIsCertNotFound` (exit 4).
- `internal/core/infra_test.go`:
  - Reescrito con 13 test cases.
  - `fakeInfraAPI` + `fakeComposeRunner` con funciones inyectables para `runFn` y `runToFn`.
  - Tests cubren: status healthy/degraded, backup success, backup partial-removal-on-error, upgrade success, upgrade pre-backup failure preserves `db_unavailable` code, upgrade health-timeout rollback, restore path, doctor path, streaming integrity, etc.
- `internal/errs/errors_test.go`:
  - `TestExitCode_KnownCodes` reemplazado: tabla nueva con codes 8/9/10/11 y confirm=2, db_unavailable=6, etc.
- `internal/cli/confirm_test.go` (NEW): 11 tests cubriendo Confirm (5 ramas), RunHandler (3 paths), y dry-run gate (2 paths).

### Batch E — polish + final (DONE)
- `.goreleaser.yaml`: `ldflags:` movido de `flags:` (forma canónica, evita override de Go). Vars `main.version/commit/date` añadidas en main.go.
- `gofmt -w .` ejecutado. `gofmt -l .` → vacío.
- `go build ./...` → limpio.
- `go vet ./...` → sin hallazgos.
- `go test ./... -count=1` → 88 pass / 0 fail.
- `go test -race ./... -count=1` → 88 pass / 0 fail.
- `LICENSE` ya existía (verificado en sesión anterior).

## Out of scope (no tocado)

- Backend .NET (`/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/src/`) — sin cambios.
- Comandos v2/v3, rediseño de UX, nuevos subcomandos.
- Endpoints / rutas / campos multipart de bootstrap / gate readonly / modelo config-secret / separación handler↔render.

## Notas para verify

- Cada fix de contrato cita la evidencia `.cs` en `design.md`; el verify DEBE re-correr contra el backend real.
- `apply-progress.md` documenta las decisiones y batches; NO es el `verify-report.md` (eso se generará en un sdd-verify separado si el usuario lo lanza).
