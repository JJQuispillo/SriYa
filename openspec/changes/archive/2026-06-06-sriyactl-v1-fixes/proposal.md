# Proposal: sriyactl v1 fixes — hacer la CLI shippable y restaurar la confianza en el contrato

## Intent

La CLI `sriyactl` (repo hermano `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/sriyactl/`) compila y sus tests pasan, pero **NO es shippable**. La arquitectura es sólida (separación handler↔render, gate readonly, modelo de config/secrets, sin shell-injection), pero el `verify` previo dio el OK contra un **contrato de backend FABRICADO**: los tests asumen status HTTP y DTOs inventados, no los reales del backend .NET en `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/src/Qora.Billing.Api/`.

**Mandato transversal (no negociable):** toda suposición sobre el contrato del backend DEBE re-verificarse contra el backend .NET real ANTES/DURANTE el fix. Los tests actuales que afirman el 409 inventado y los DTOs equivocados se CORRIGEN al contrato real (no se conservan). Tras los fixes, re-correr `verify` (`go test ./...`, `go build ./...`).

## Scope

### In Scope — los 11 findings, agrupados por severidad

**BLOCKERS**
1. `infra restore` ejecuta SIN confirmación. `--yes`/`--no-input` están declarados (`internal/cli/middleware.go:29-30`, `internal/cli/root.go:29-30`) pero nunca se leen; no hay prompt TTY. Fix: ops destructivas confirman salvo `--yes`/`--no-input` o non-TTY con intención explícita; gate ANTES del side-effect.
2. `cert status` marca TODO cert como expirado. El struct Go `Certificate` (`internal/api/client.go:76-83`) espera `subject, issuer, expiresAt, estado`; el backend `CertificateResponse` (`billing/src/.../CertificateDtos.cs`) serializa `id, nombrePropietario, fechaExpiracion, activo, fechaCreacion`. `ExpiresAt` decodifica a zero-time → siempre "expired". Fix: alinear json tags (Subject←nombrePropietario, ExpiresAt←fechaExpiracion, Status desde fechaExpiracion+activo).
3. RUC duplicado mapeado a HTTP 409 (`internal/api/client.go:184`), pero el backend devuelve **400 BadRequest** para RUC duplicado (`GlobalExceptionHandler.cs:106-108`; el único 409 es `SecuencialExhaustedException`, no relacionado). `tenant create` duplicado da exit 1 genérico volcando ProblemDetails crudo. Fix: detectar 400 + señal de duplicado en ProblemDetails → `tenant_duplicate`/exit 5; eliminar rama 409 muerta.

**HIGH**
4. `RunHandler` (`internal/cli/middleware.go:86-90`) descarta `out` cuando `err != nil`. Handlers con dato+error-centinela (`internal/core/cert.go:135-148` expiring/expired; `internal/core/infra.go:87-93` degraded) pierden el payload. Fix: renderizar `out` no vacío a stdout, luego error a stderr, luego retornar exit code.
5. `infra status` fabrica readiness (`internal/core/infra.go:69`): llama `Health` dos veces, sin `/health/ready`. Backend tiene `GET /health/ready`. Fix: añadir `Ready(ctx)` al `api.Client` que pegue `/health/ready` y cablearlo.

**MEDIUM**
6. `cert status` sin manejo de lista vacía; backend devuelve `200 []` para tenant sin cert → exit 0 en vez de `cert_not_found`. La rama 404 (`client.go:217`) es muerta. Fix: `len(raw)==0` → CodeCertNotFound.
7. `infra upgrade` (`internal/core/infra.go:183-242`) nunca hace el backup pre-upgrade mandatorio ("backup→bump tag→pull→up") ni avisa. Fix: backup (o exigir backup reciente) antes de mutar `.env`.
8. `infra backup` (`internal/core/infra.go:274-283`) bufferea todo `pg_dump` en un string Go y luego escribe; DBs grandes inflan memoria y el buffer de texto corrompe dumps binarios/locale. Fix: stream de stdout de pg_dump directo a archivo (`RunTo(w io.Writer)`).

**LOW**
9. Exit codes (`internal/errs/errors.go:122`): cert_expiring/cert_expired y upgrade_health_timeout/doctor_check_failed colapsan en exit 6 (clase network/retryable). CI no los distingue. Fix: códigos no-cero estables y distintos.
10. 11 archivos sin formatear (`gofmt -l .`): client.go, middleware.go, wiring.go, runner.go, cert.go (core), infra.go, infra_test.go, tenant.go, testhelpers_test.go, errors.go. Fix: `gofmt -w .`.
11. `.goreleaser.yaml`: sin bloque `brews:` (Success Criteria de la propuesta exigía brew tap); forma repetida `-ldflags=-X` puede no inyectar `main.version`; asegurar que `LICENSE` exista (los archives lo referencian). Fix según corresponda.

### Out of Scope (CONFIRMED-CORRECT — NO tocar)
- Rutas de endpoints (`/api/v1/bootstrap`, `/api/v1/certificates`, `/health`) coinciden con el backend.
- Campos multipart de bootstrap (ruc, razonSocial, ownerName, password, certificate, opcionales nombreComercial/correoContacto/apiKeyName) + header `X-Service-Token` correctos.
- Separación handler↔render, gate readonly, modelo config/secret, no shell-injection.
- Rediseño de comandos, features nuevas, cambios al backend .NET.

## Approach

Re-verificación contra el backend real PRIMERO, luego fix por severidad (blockers → high → medium → low). Cada fix de contrato (#2, #3, #5, #6) acompañado de **tests corregidos al contrato real** (DTOs y status reales), reemplazando las aserciones fabricadas. Cerrar con `gofmt -w .`, `go build ./...`, `go test ./...` y un re-`verify` completo.

## Affected Areas

| Area | Impacto | Descripción |
|------|---------|-------------|
| `internal/api/client.go` | Modified | DTO Certificate (#2), mapeo 400 duplicado (#3), `Ready()` (#5), `RunTo()` (#8), borrar ramas muertas 409/404 |
| `internal/cli/middleware.go`, `root.go` | Modified | leer `--yes`/`--no-input` + prompt TTY (#1), render-then-error en RunHandler (#4) |
| `internal/core/infra.go` | Modified | readiness real (#5), backup pre-upgrade (#7), stream backup (#8) |
| `internal/core/cert.go` | Modified | empty-list → cert_not_found (#6), payload en sentinel (#4) |
| `internal/errs/errors.go` | Modified | exit codes distintos (#9) |
| `**/*_test.go` | Modified | tests al contrato real (#2,#3,#5,#6) — reemplazar aserciones fabricadas |
| `.goreleaser.yaml`, `LICENSE` | Modified/New | bloque brews, ldflags version, LICENSE (#11) |
| Todos los `.go` listados | Modified | `gofmt -w .` (#10) |

## Risks

| Riesgo | Probabilidad | Mitigación |
|--------|--------------|------------|
| Re-introducir suposiciones inventadas | Media | Cada fix de contrato cita evidencia del backend (.cs file:line); tests usan payloads reales |
| Confirmación rompe automatización/CI | Media | `--yes`/`--no-input` y non-TTY explícito como bypass; default seguro |
| Cambio de exit codes rompe scripts existentes | Baja | Aún no shippable v1; documentar tabla estable antes de release |
| Stream backup cambia semántica de errores | Baja | Cubrir con test; propagar error de pg_dump tras escribir |

## Rollback Plan

Cada finding es un commit atómico → revertir el commit ofensivo. Repo no liberado (sin tag v1), así que no hay consumidores aguas abajo. Si el re-`verify` falla, no se taggea release y se itera.

## Dependencies

- Backend .NET real en `/Users/jjquispi/Documents/proyectos_personales/P.O.S.Qora/billing/src/Qora.Billing.Api/` como fuente de verdad del contrato (CertificateDtos.cs, GlobalExceptionHandler.cs, endpoints `/health/ready`).
- Toolchain Go + `gofmt` + goreleaser.

## Success Criteria

- [ ] Contrato re-verificado contra el backend real; cada fix de contrato cita evidencia `.cs` file:line.
- [ ] Los 11 findings resueltos (3 blockers, 2 high, 3 medium, 3 low).
- [ ] `cert status` reporta estado real; lista vacía → cert_not_found.
- [ ] `tenant create` duplicado → tenant_duplicate/exit 5 (no 409).
- [ ] `infra restore` confirma salvo `--yes`/`--no-input`/non-TTY explícito.
- [ ] `infra status` usa `/health/ready` real; handlers con payload+sentinel renderizan el payload.
- [ ] `infra backup` hace streaming; `infra upgrade` respalda antes de mutar.
- [ ] Exit codes distintos y estables; `gofmt -l .` vacío.
- [ ] `.goreleaser.yaml` con brews + ldflags version; LICENSE presente.
- [ ] Tests CORREGIDOS al contrato real (no conservan 409/DTO fabricados); `go build ./...` y `go test ./...` verdes; re-`verify` OK.
