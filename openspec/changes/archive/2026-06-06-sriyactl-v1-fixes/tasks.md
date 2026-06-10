# Tasks: sriyactl v1 fixes

> **Repo objetivo (CLI):** `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/`
> Todos los `file:path` de tareas son relativos a ese repo salvo cuando se cita el backend `.cs` (ubicado en `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/src/`).
>
> **Alcance:** SOLO los 11 findings de la propuesta. NO se agregan features v2/v3, ni se rediseñan comandos, ni se toca el backend .NET. Las rutas de endpoints, los campos multipart de bootstrap, el gate readonly, el modelo config/secret y la separación handler↔render se conservan tal cual (Out of Scope, CONFIRMED-CORRECT).
>
> **Mandato transversal:** cada fix de contrato cita la evidencia `.cs` ya verificada. El apply NO debe re-adivinar el contrato. Los tests que afirman el contrato fabricado (409 duplicado, DTO cert equivocado, `status=="ok"`) se REESCRIBEN al contrato real, no se conservan.

## Contrato real del backend (hechos verificados — usar sin re-adivinar)

- **RUC duplicado → HTTP 400** `BillingDomainException` (`GlobalExceptionHandler.cs:106-112`); `Detail` contiene `"Ya existe un tenant con el RUC"` (`TenantBootstrapService.cs:67`). El único **409** es `SecuencialExhaustedException` (`:98-104`), NO relacionado con duplicados.
- **Certificate JSON (camelCase):** `id, nombrePropietario, fechaExpiracion, activo, fechaCreacion` (`CertificateDtos.cs:3-8`). NO existen `subject/issuer/estado/expiresAt`.
- **Lista de certs vacía → backend `200 []`** (`CertificateEndpoints.cs:72-80`), NUNCA 404.
- **Health:** `GET /health` → `{"status":"Healthy"}`; `GET /health/ready` → `{"status":"Ready"}` (200) o **503** sin cuerpo útil (`HealthEndpoints.cs:20-49`). El status es PascalCase; comparar contra `"Healthy"`/`"Ready"`, NO contra `"ok"`.

---

## Fase 1 — BLOCKERS (hacer primero)

### 1.1 — Confirmación de operaciones destructivas (finding #1)
> Spec: `specs/infra/spec.md` → **Requirement: infra restore**; `specs/ai-contract/spec.md` → **Requirement: dry-run y modo no-interactivo**. Design §#1 (tabla de decisión).

- [x] Crear `internal/cli/confirm.go` con el helper único `Confirm(flags SharedFlags, stdin io.Reader, stdout io.Writer, resourceDesc string) error` siguiendo Design §#1: si `flags.Yes || flags.NoInput` → `nil` (bypass); si stdin NO es TTY (`term.IsTerminal`) y sin bypass → `errs.New(errs.CodeConfirmRequired, ...)`; si TTY interactivo → prompt `"About to <desc>. Continue? [y/N]: "`, aceptar solo `y`/`yes` (case-insensitive), cualquier otra cosa → `errs.New(errs.CodeConfirmAborted, ...)`.
- [x] Leer realmente los flags `--yes`/`--no-input` que hoy son dead-code: confirmar su declaración en `internal/cli/middleware.go:29-30` y `internal/cli/root.go:29-30` y cablearlos al struct `SharedFlags` que recibe `Confirm` (hoy se declaran pero nunca se leen).
- [x] Invocar `Confirm` en el path mutador, NO dentro de cada handler: en `internal/cli/middleware.go` `RunHandler` (o el wrapper del comando destructivo), DESPUÉS de `BuildContext()` y `GuardMutation`, y ANTES de `handler(ctx,in)`. Solo se dispara cuando el comando marca `RequiresConfirm`.
- [x] Respetar la precedencia de Design §#1: `GuardMutation` (readonly / `SRIYACTL_READONLY`) corre PRIMERO y gana (exit 7 sin llegar al prompt); `--dry-run` precede y NO requiere confirmación (no muta); solo `infra restore` (y, por #7, el path mutador de `infra upgrade`) marca `RequiresConfirm`.
- [x] Marcar `infra restore` como `RequiresConfirm` en su wiring de comando (`internal/cli/wiring.go` / definición del comando restore) para que el gate se active antes del side-effect en `internal/core/infra.go`.

### 1.2 — Realineación del DTO Certificate + derivación de Status (finding #2)
> Spec: `specs/cert/spec.md` → **Requirement: cert status**. Design §#2. Evidencia: `CertificateDtos.cs:3-8`.

- [x] En `internal/api/client.go` reemplazar el struct `Certificate` (`:76-83`) por los campos REALES con sus json tags camelCase: `ID json:"id"`, `NombrePropietario json:"nombrePropietario"`, `FechaExpiracion time.Time json:"fechaExpiracion"`, `Activo bool json:"activo"`, `FechaCreacion time.Time json:"fechaCreacion"`. Eliminar `Subject`/`Issuer`/`ExpiresAt`/`Estado` (no existen en el backend).
- [x] En `internal/core/cert.go` (`CertStatusHandler`, ~`:135-148`) derivar `Status` a partir de `FechaExpiracion` + `Activo` + `--warn-days` (Design §#2): `!Activo` → `expired`; `hours < 0` → `expired`; `días <= warn` → `expiring`; else → `valid`. Comparar siempre en UTC (`time.Now().UTC()`); si `FechaExpiracion.Kind==Unspecified`/sin `Z`, tratar como UTC.
- [x] En la entry de salida (`CertStatusEntry`): alimentar la columna descriptiva desde `NombrePropietario`. Resolver la Open Question del design: quitar la columna `Issuer` (recomendado, el backend no la expone) o dejarla vacía — preferir quitarla para no exponer un campo muerto.
- [x] Verificar que el bug del "zero-time → todo expired" desaparece: un cert con `fechaExpiracion` futura fuera de warn → `valid`.

### 1.3 — Mapeo de RUC duplicado 400 + eliminar rama 409 (finding #3)
> Spec: `specs/tenant/spec.md` → **Requirement: tenant create**. Design §#3. Evidencia: `GlobalExceptionHandler.cs:106-112`, `TenantBootstrapService.cs:67`.

- [x] En `internal/api/client.go` `BootstrapTenant`: ELIMINAR la rama muerta `resp.StatusCode == http.StatusConflict` (`:184-186`). El backend NUNCA devuelve 409 para duplicados.
- [x] En el caller de bootstrap (NO en el `mapHTTPError` genérico), ante un **400** parsear el cuerpo `ProblemDetails {type,title,detail}` y aplicar la heurística acotada de Design §#3: si `strings.ToLower(detail)` contiene `"ya existe un tenant"` → `errs.New(CodeTenantDuplicate, ...)` (exit 5). Esto evita falso positivo con RUC inválido (`InvalidRucException`, Detail distinto) y con cert/password inválidos.
- [x] Fallback: cualquier otro 400 → `CodeBootstrapBadReq` con el `Detail` verbatim del ProblemDetails (NO volcar el ProblemDetails crudo como exit 1 genérico).
- [x] Garantizar que en el caso duplicado NO se registra alias en config ni se escribe el `apiKey` en el keychain (per spec).

---

## Fase 2 — HIGH

### 2.1 — RunHandler: render de payload + error-centinela (finding #4)
> Spec: `specs/ai-contract/spec.md` → **Requirement: payload con centinela no se descarta**. Design §#4 (patrón `Renderable()` sentinel).

- [x] En `internal/errs/errors.go` añadir la interfaz `Renderable interface{ Renderable() bool }`, el campo `RenderPayload bool` en `CLIError`, el método `func (e *CLIError) Renderable() bool { return e != nil && e.RenderPayload }` y el setter `func (e *CLIError) MarkRenderable() *CLIError { e.RenderPayload = true; return e }`.
- [x] En `internal/cli/middleware.go` `RunHandler` (`:86-90`) rediseñar el manejo de error: cuando `err != nil`, si `errors.As(err, &r)` y `r.Renderable()` → `render.Render(stdout, out, format)` PRIMERO (payload a stdout), luego `render.RenderError(stderr, err, format)`, y recién entonces `return errs.ExitCode(err)`. Los errores fatales (auth, usage, network duro) NO marcan renderable → comportamiento actual (solo stderr). El mapeo exit-code NO cambia.
- [x] En `internal/core/cert.go` marcar `MarkRenderable()` en los sentinels `expiring`/`expired` (`:135-148`) para que el payload del cert se emita junto a la señal.
- [x] En `internal/core/infra.go` marcar `MarkRenderable()` en el sentinel `degraded`/`ready=down` (`:87-93`) para emitir el payload de status.

### 2.2 — Readiness real vía /health/ready + fix comparación de status (finding #5)
> Spec: `specs/infra/spec.md` → **Requirement: infra status**. Design §#5. Evidencia: `HealthEndpoints.cs:20-49`.

- [x] En `internal/api/client.go` añadir `Ready(ctx context.Context) (Health, error)` que pegue `GET /health/ready`: 200 → `Health{Status:"Ready",...}`; **503** → `CLIError(CodeDBUnavailable)` no-fatal. El struct `Health` NO tiene `serviceTag` (no existe en el backend) — eliminarlo o `omitempty`.
- [x] En `internal/core/infra.go` `InfraStatusHandler` (`:69`) reemplazar el SEGUNDO `d.API.Health` por `d.API.Ready(ctx)`. Hoy llama `Health` dos veces y fabrica readiness.
- [x] **Fix crítico de comparación:** corregir el chequeo de liveness que hoy compara `Status == "ok"` (valor inventado) → comparar `Status == "Healthy"` para liveness y `Status == "Ready"` para readiness (PascalCase real, Design §#5).
- [x] Llenar `InfraStatusResult{Health, Ready, Degraded}`: si readiness 503/ausente → `Degraded=true` → sentinel renderable (#4) → exit clase network/6 emitiendo el payload.

---

## Fase 3 — MEDIUM

### 3.1 — Lista de certs vacía → cert_not_found (finding #6)
> Spec: `specs/cert/spec.md` → **Requirement: cert status** (escenario "tenant sin certificado"). Design §#6. Evidencia: `CertificateEndpoints.cs:72-80`.

- [x] En `internal/core/cert.go` `CertStatusHandler`, tras `raw, err := d.API.CertStatus(...)`: si `err == nil && len(raw) == 0` → `errs.New(CodeCertNotFound, ...)` con `hint` para subir el certificado (exit 4, no-cero). NO retornar exit 0 con lista vacía.
- [x] En `internal/api/client.go` ELIMINAR la rama muerta `resp.StatusCode == http.StatusNotFound` (`:217`); el backend nunca devuelve 404 en este endpoint.

### 3.2 — Backup pre-upgrade obligatorio (finding #7)
> Spec: `specs/infra/spec.md` → **Requirement: infra upgrade**. Design §#7.

- [x] En `internal/core/infra.go` `InfraUpgradeHandler` (`:183-242`) imponer el orden mandatorio: `GuardMutation` → validar `--to` → **Confirm (#1)** → **backup (invocar la lógica de #8) o exigir backup reciente** → escribir `BILLING_IMAGE_TAG` → `pull` → `up -d` → esperar `GET /health/ready` OK.
- [x] Si el backup falla (o no hay backup reciente) → abortar ANTES de mutar `.env` (no se toca nada).
- [x] Marcar el path mutador de `infra upgrade` como `RequiresConfirm` (coordina con tarea 1.1).
- [x] Mantener el rollback existente: si `/health/ready` no recupera dentro del timeout → restaurar el tag anterior y fallar con `CodeUpgradeHealthTimeout` (ver 4.1).

### 3.3 — Backup en streaming con limpieza de archivo parcial (finding #8)
> Spec: implícito en **Requirement: infra upgrade** (backup) + propuesta finding #8. Design §#8.

- [x] En `internal/compose/runner.go` añadir `RunTo(ctx context.Context, w io.Writer, args ...string) error` a la interfaz `Runner` y a `ExecRunner`: `cmd.Stdout = w` (sin buffer de texto), `cmd.Stderr = &buf` (capturar para el error). Conservar `Run` existente para `ps`/`pull`/`up`.
- [x] En `internal/core/infra.go` `InfraBackupHandler` (`:274-283`) abrir el `*os.File` destino y pasarlo como `w` a `RunTo` para que `pg_dump` streamee directo (binary-safe; sin `string`/`bytes.Buffer` intermedio que corrompa dumps binarios/locale).
- [x] Manejo de fallo a mitad de stream: si `RunTo` retorna error → cerrar el archivo y `os.Remove(fullPath)` (limpieza del parcial) ANTES de propagar el `CLIError`.

---

## Fase 4 — LOW / Polish

### 4.1 — Exit codes distintos y estables (finding #9)
> Spec: `specs/ai-contract/spec.md` → **Requirement: exit codes distintos y estables por clase**. Design §#9.

- [x] En `internal/errs/errors.go` (`codeToExit`, ~`:122`) desambiguar la tabla `code→exit` manteniendo 0-7 existentes y añadiendo: `cert_expiring`→**8**, `cert_expired`→**9**, `upgrade_health_timeout`→**10**, `doctor_check_failed`→**11**. `confirm_required`/`confirm_aborted`→**2** (usage). `tenant_duplicate`→**5**.
- [x] Añadir los nuevos `Code`: `CodeConfirmRequired`, `CodeConfirmAborted` (exit 2). El envelope JSON `{code,message,hint,retryable}` NO cambia; solo el mapeo `code→exit` se desambigua.

### 4.2 — gofmt (finding #10)
- [x] Ejecutar `gofmt -w .` sobre el repo, asegurando que quedan formateados los 11 archivos reportados: `internal/api/client.go`, `internal/cli/middleware.go`, `internal/cli/wiring.go`, `internal/cli/runner.go`, `internal/core/cert.go`, `internal/core/infra.go`, `internal/core/infra_test.go`, `internal/core/tenant.go`, `internal/core/testhelpers_test.go`, `internal/errs/errors.go` (y el nuevo `internal/cli/confirm.go`).

### 4.3 — goreleaser + LICENSE (finding #11)
- [x] En `.goreleaser.yaml` añadir el bloque `brews:` (Success Criteria de la propuesta exige brew tap).
- [x] Corregir el `ldflags` para que inyecte `main.version`: usar la forma `-ldflags "-X main.version={{.Version}}"` (la forma repetida `-ldflags=-X` puede no inyectar).
- [x] Garantizar que `LICENSE` existe en la raíz del repo (los archives lo referencian); crearlo si falta.

---

## Fase 5 — TESTS al contrato REAL (críticos — reemplazan los fabricados)

> Spec: `specs/ai-contract/spec.md` → **Requirement: tests afirman el contrato real del backend**. Design §"Test impact". Los tests previos que afirman 409 / DTO cert equivocado / `status=="ok"` se ELIMINAN, no se conservan.

- [x] **Duplicado:** en `internal/core/tenant.go`/su `_test.go` REEMPLAZAR el test que afirma **409**/`CodeConflict` por uno que stubbea `400` + `ProblemDetails` con `Detail="Ya existe un tenant con el RUC '...'"` → aserta `CodeTenantDuplicate`/exit 5. Añadir caso negativo: `400` con RUC inválido (Detail distinto) → NO debe mapear a duplicate. El test de 409 NO debe seguir existiendo.
- [x] **Cert DTO:** REEMPLAZAR fixtures que construyen `Certificate{Subject,Issuer,ExpiresAt,Estado}` o json `subject/issuer/expiresAt/estado` por los campos reales `id/nombrePropietario/fechaExpiracion/activo/fechaCreacion`. Añadir caso "cert vigente (fechaExpiracion futura) → `valid`" para probar que el bug del zero-time→expired desapareció.
- [x] **Cert empty list:** añadir test `200 []` → `CodeCertNotFound`/exit 4; eliminar/ajustar cualquier test que asuma la rama 404.
- [x] **Health/Ready:** REEMPLAZAR tests que esperan `status=="ok"` o `serviceTag` por `"Healthy"`/`"Ready"`; añadir fake para `GET /health/ready` con caso **503** → `ready=down`/degraded (endpoint distinto de `/health`).
- [x] **Confirmación (3 ramas):** añadir tests para el gate de `Confirm`: (a) TTY sin `--yes` y respuesta `n`/vacío → abort sin side-effect, exit no-cero; (b) `--yes`/`--no-input` → procede sin prompt; (c) non-TTY sin `--yes` → `CodeConfirmRequired`, exit no-cero, SIN efecto. Usar `stdin`/`stdout` inyectables y un fake de TTY.
- [x] **RunHandler payload+sentinel:** añadir test que verifique que un sentinel renderable imprime el payload a **stdout** Y el error a **stderr** con exit no-cero; y que un error fatal NO imprime payload.
- [x] **Streaming backup error path:** añadir test que stubbea `RunTo` retornando error a mitad de stream → verifica que el archivo parcial se `os.Remove` (no queda en disco) y se propaga el `CLIError`.
- [x] **Exit codes:** actualizar/añadir tests que aserten `cert_expiring`→8, `cert_expired`→9, `upgrade_health_timeout`→10, `doctor_check_failed`→11 (los que afirmaban 6 deben cambiar).

---

## Fase 6 — Re-verificación (cierre)

> El verify previo confió en status codes adivinados y NO debe reutilizarse. Comandos de `openspec/config.yaml`.

- [x] `gofmt -l .` → salida VACÍA (sin archivos pendientes).
- [x] `go vet ./...` → sin hallazgos.
- [x] `go test ./...` → verde (con los tests reescritos al contrato real).
- [x] `go build ./...` → compila limpio.
- [ ] Re-correr un SDD `verify` COMPLETO contra el backend .NET REAL (no reutilizar el verify previo, que firmó contra el contrato fabricado). Cada fix de contrato debe citar su evidencia `.cs` file:line.

---

## Definition of Done

- [ ] Los 11 findings resueltos (3 blockers, 2 high, 3 medium, 3 low).
- [ ] `infra restore` confirma salvo `--yes`/`--no-input`; non-TTY sin bypass rehúsa con `confirmation_required`.
- [ ] `cert status` reporta estado real desde `fechaExpiracion` (cert vigente → `valid`, nunca `expired` por bug); lista vacía → `cert_not_found`/exit no-cero.
- [ ] `tenant create` duplicado → `tenant_duplicate`/exit 5 (no 409, no exit 1 crudo); rama 409 eliminada.
- [ ] `RunHandler` emite payload de sentinels renderables a stdout + error a stderr + exit no-cero.
- [ ] `infra status` usa `/health/ready` real y compara `"Healthy"`/`"Ready"` (no `"ok"`).
- [ ] `infra backup` streamea (binary-safe, limpia parcial en error); `infra upgrade` respalda antes de mutar `.env`.
- [ ] Exit codes distintos y estables (8/9/10/11 + confirm=2 + tenant_duplicate=5).
- [ ] `gofmt -l .` vacío; `.goreleaser.yaml` con `brews:` + ldflags version corregido; `LICENSE` presente.
- [ ] Tests CORREGIDOS al contrato real (sin 409/DTO fabricados/`status=="ok"`); `go test ./...` y `go build ./...` verdes; re-`verify` OK contra backend real.
- [ ] Alcance respetado: SOLO estos fixes — sin features nuevas v2/v3 ni cambios al backend .NET.
