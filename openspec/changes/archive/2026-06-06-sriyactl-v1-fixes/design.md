# Design: sriyactl v1 fixes — corrección de contrato y hardening

## Technical Approach

Diseño correctivo sobre la CLI Go existente. Se mantienen TODOS los patrones del diseño original (`archive/2026-06-06-sriyactl-cli/design.md`): separación handler↔render, `Output[T]`, gate `GuardMutation`, `errs.CLIError` como única fuente de exit code, interfaces inyectables (`api.Client`, `compose.Runner`). Cada fix de contrato (#2, #3, #5, #6) se ancla a evidencia `.cs` verificada (abajo). No se introduce arquitectura nueva: se corrigen DTOs, mapeos y un punto del pipeline.

**Contrato real verificado** (fuente de verdad):
- `CertificateDtos.cs:3-8` → `CertificateResponse(Id, NombrePropietario, FechaExpiracion, Activo, FechaCreacion)`; serializado camelCase ASP.NET → `id, nombrePropietario, fechaExpiracion, activo, fechaCreacion`. **No existen** `subject/issuer/estado/expiresAt`.
- `GlobalExceptionHandler.cs:106-112` → `BillingDomainException` ⇒ **400**, `Title="Error de facturación"`. `TenantBootstrapService.cs:67` lanza `BillingDomainException("Ya existe un tenant con el RUC '...'")`. El **único 409** es `SecuencialExhaustedException` (`:98-104`, `Title="No se pudo asignar el secuencial"`), no relacionado con duplicados.
- `HealthEndpoints.cs` → `/health` ⇒ `{"status":"Healthy","timestamp":...}`; `/health/ready` ⇒ `{"status":"Ready",...}` (200) o **503** sin cuerpo útil si la DB no conecta. **No hay** `serviceTag`; el valor del status es PascalCase (`Healthy`/`Ready`).

## Architecture Decisions

| # | Decisión | Elegido | Rechazado | Rationale |
|---|----------|---------|-----------|-----------|
| 1 | Confirmación destructiva | Helper único `Confirm(ctx, flags, stdin, stdout, desc)` invocado en el path mutador (middleware), no por comando | Prompt ad-hoc en cada handler | "AI-contract once": una sola decisión, testeable; consistente con `GuardMutation` |
| 2 | Cert Status | Derivar `Status` de `fechaExpiracion`+`activo`+`--warn-days` en el handler; DTO solo transporta | Confiar en un campo `estado` del backend | El backend no envía estado calculado; `activo` es booleano de revocación, no de vigencia |
| 3 | Mapeo duplicado | 400 + match sobre `Detail`/`Title` del ProblemDetails con marcador acotado | Status-only; o match laxo "RUC" | Evita falso positivo con otros 400 (RUC inválido, cert inválido) |
| 4 | Render+sentinel | Sentinel tipado `Renderable() bool` sobre `*CLIError` | Segundo valor de retorno booleano | No cambia firmas de handler; el pipeline decide por interfaz |
| 5 | Readiness | `Ready(ctx)` separado que pega `/health/ready`; 503 ⇒ degraded no-fatal | Reusar `Health` dos veces | El doble-Health actual fabrica readiness |
| 6 | Backup streaming | `RunTo(ctx, w io.Writer, args...)` en `Runner` | Bufferear stdout en string | Dumps binarios/locale + DBs grandes |

## Fix-by-Fix Design

### #1 — Confirmación de ops destructivas (BLOCKER)

Helper único en `internal/cli` (o `internal/core`), invocado **antes** del side-effect, en el wrapper mutador del comando (junto a donde se setea `Mutating=true`), NO dentro de cada handler:

```go
// internal/cli/confirm.go
func Confirm(flags SharedFlags, stdin io.Reader, stdout io.Writer, resourceDesc string) error {
    if flags.Yes || flags.NoInput { return nil }          // bypass explícito (CI/agente)
    if !term.IsTerminal(int(os.Stdin.Fd())) {             // no-TTY sin --yes
        return errs.New(errs.CodeConfirmRequired,
            "destructive operation requires confirmation",
            "re-run with --yes / --no-input (non-interactive)")
    }
    fmt.Fprintf(stdout, "About to %s. Continue? [y/N]: ", resourceDesc)
    // leer línea; aceptar solo y/yes (case-insensitive); else CodeConfirmAborted
}
```

**Tabla de decisión:**

| Entorno | `--yes`/`--no-input` | Acción |
|---------|----------------------|--------|
| TTY interactivo | no | prompt `[y/N]`; `n`/vacío ⇒ abort (exit usage) |
| TTY interactivo | sí | proceder |
| non-TTY (CI/pipe) | no | **rehusar** con `CodeConfirmRequired` |
| non-TTY | sí | proceder |

**Plug-in point:** se llama en `RunHandler` (o el wrapper del comando destructivo) tras `BuildContext()` y **antes** de `handler(ctx,in)`. Interacción con readonly: `GuardMutation` (readonly/`SRIYACTL_READONLY`) corre **primero** dentro del handler y gana — un contexto readonly aborta con exit 7 sin llegar a prompt. `--dry-run` también precede y no requiere confirmación (no muta). Solo `infra restore` (y, por #7, el path de mutación de `infra upgrade`) marca `RequiresConfirm`.

### #2 — Realineación DTO Certificate (BLOCKER)

```go
// internal/api/client.go
type Certificate struct {
    ID                string    `json:"id"`
    NombrePropietario string    `json:"nombrePropietario"`
    FechaExpiracion   time.Time `json:"fechaExpiracion"`
    Activo            bool      `json:"activo"`
    FechaCreacion     time.Time `json:"fechaCreacion"`
}
```

`Subject` (en `CertStatusEntry`) se alimenta de `NombrePropietario`; se eliminan `Issuer`/`Estado` del DTO de API (Issuer ya no existe en el contrato; mantener `Issuer:""` en la entry o quitar la columna). **Derivación de Status** en `CertStatusHandler` usando `FechaExpiracion` + `Activo` + `warn`:

```
now := time.Now().UTC()
hours := c.FechaExpiracion.Sub(now).Hours()
switch {
case !c.Activo:        st = "expired"  // revocado/inactivo ⇒ no usable
case hours < 0:        st = "expired"
case days <= warn:     st = "expiring"
default:               st = "valid"
}
```

**Parse/timezone:** `DateTime` UTC ASP.NET serializa ISO-8601 (`2026-01-02T15:04:05Z` o con offset); `encoding/json` lo decodifica a `time.Time` directo. Comparar siempre en UTC (`now.UTC()`). Si el backend emitiera sin `Z` (Kind=Unspecified), tratar como UTC.

### #3 — Mapeo de error desde ProblemDetails real (BLOCKER)

El backend devuelve **400** con cuerpo ProblemDetails `{type,title,detail,...}`. El duplicado tiene `Title="Error de facturación"` y `Detail="Ya existe un tenant con el RUC '...'"`. No hay campo `code` estable (limitación conocida del diseño original, task 8.6).

**Heurística acotada en el caller de bootstrap** (no en `mapHTTPError` genérico): parsear el cuerpo a un struct ProblemDetails y matchear sobre `Detail` con marcador específico:

```go
func looksLikeTenantDuplicate(pd problemDetails) bool {
    d := strings.ToLower(pd.Detail)
    return strings.Contains(d, "ya existe un tenant") || strings.Contains(d, "ruc")
        && strings.Contains(d, "existe")  // acota: "existe"+"ruc" juntos
}
```

Precisión: matchear `"ya existe un tenant"` evita el falso positivo con RUC **inválido** (`InvalidRucException`, Detail distinto) y con cert/password inválidos. Si match ⇒ `CodeTenantDuplicate`/exit 5. **Fallback:** cualquier otro 400 ⇒ `CodeBootstrapBadReq` con `Detail` verbatim. **Eliminar** la rama muerta `resp.StatusCode == http.StatusConflict` en `BootstrapTenant` (`client.go:184-186`).

### #4 — Render de dato + error-centinela (HIGH)

`RunHandler` (`middleware.go:86-90`) descarta `out` cuando `err!=nil`. Rediseño: distinguir centinelas "render-and-signal" de errores fatales vía interfaz sobre el error.

```go
// internal/errs
type Renderable interface{ Renderable() bool }
func (e *CLIError) Renderable() bool { return e != nil && e.RenderPayload }
func (e *CLIError) MarkRenderable() *CLIError { e.RenderPayload = true; return e }
```

`RunHandler`:
```go
out, err := handler(ctx, in)
if err != nil {
    var r errs.Renderable
    if errors.As(err, &r) && r.Renderable() {
        _ = render.Render(stdout, out, format)   // payload a stdout primero
    }
    _ = render.RenderError(stderr, err, format)  // luego error a stderr
    return errs.ExitCode(err)                    // exit code intacto
}
```

Los sentinels de `cert.go` (expiring/expired) e `infra.go` (degraded) marcan `MarkRenderable()`. Errores fatales (auth, usage, network duro) NO lo marcan ⇒ comportamiento actual (solo stderr). Exit-code mapping inalterado.

### #5 — Probe `/health/ready` (HIGH)

Añadir a `api.Client`:
```go
Ready(ctx context.Context) (Health, error)  // GET /health/ready
```
`Health{Status,Timestamp}` (el campo `serviceTag` no existe; mantenerlo opcional/omitempty o eliminarlo). 200 ⇒ `{status:"Ready"}`; **503** ⇒ devolver `CLIError(CodeDBUnavailable)` no-fatal. `InfraStatusHandler`: reemplazar el segundo `d.API.Health` (`infra.go:69`) por `d.API.Ready(ctx)`; `out.Ready` se llena si 200; readiness 503 o ausente marca `out.Degraded=true`. **Importante:** la comprobación de liveness actual compara `Status != "ok"` — corregir a `Status != "Healthy"` (valor real). Forma del resultado combinado: `InfraStatusResult{Health,Ready,Degraded}` ya existe; degraded ⇒ sentinel renderable (#4) ⇒ exit network/6.

### #6 — Lista de certs vacía ⇒ cert_not_found (MEDIUM)

Backend devuelve `200 []` para tenant sin cert (`GetCertificates` ⇒ `Ok<List>`). En `CertStatusHandler`, tras `raw,err := d.API.CertStatus(...)`: si `err==nil && len(raw)==0` ⇒ `errs.New(CodeCertNotFound,...)` (exit 4). **Eliminar** la rama muerta `http.StatusNotFound` en `client.go:217` (el backend nunca devuelve 404 aquí).

### #7 — Backup pre-upgrade (MEDIUM)

`InfraUpgradeHandler` debe respaldar antes de mutar `.env`. Orden corregido: `GuardMutation` → validar `--to` → **Confirm (#1)** → **backup (invocar lógica de #8) o exigir backup reciente** → escribir tag → pull → up -d → wait `/health/ready`. Si el backup falla ⇒ abortar antes de tocar `.env` (no se muta nada). El path de upgrade que muta marca `RequiresConfirm`.

### #8 — Backup en streaming (MEDIUM)

Nuevo método en `compose.Runner`:
```go
RunTo(ctx context.Context, w io.Writer, args ...string) error
```
`ExecRunner.RunTo`: `cmd.Stdout = w` (sin buffer de texto), `cmd.Stderr = &buf` (capturar para el error). `InfraBackupHandler` abre el archivo destino, pasa el `*os.File` como `w`, y `pg_dump` streamea directo. **Binary-safety:** sin `string`/`bytes.Buffer` intermedio ⇒ no corrompe dumps binarios/locale. **Fallo a mitad de stream:** si `RunTo` retorna error, cerrar y `os.Remove(fullPath)` (limpieza de archivo parcial) antes de propagar `CLIError`. El método `Run` existente se conserva para `ps`/`pull`/`up`.

### #9 — Taxonomía de exit codes (LOW)

Hoy `codeToExit` colapsa cert_expiring/cert_expired y upgrade_timeout/doctor_check_failed en **6** (clase network/retryable). CI no los distingue. Tabla corregida (mantiene 0-7 existentes, añade 8-11 nuevos, no rompe el envelope):

| Code | Exit (nuevo) |
|------|------|
| cert_expiring | **8** |
| cert_expired | **9** |
| upgrade_health_timeout | **10** |
| doctor_check_failed | **11** |
| confirm_required / confirm_aborted | **2** (usage) |
| (resto 0-7 sin cambios) | igual |

Nuevos `Code`: `CodeConfirmRequired`, `CodeConfirmAborted` (exit 2). El envelope JSON `{code,message,hint,retryable}` no cambia; solo el `code→exit` se desambigua.

### #10 / #11 — gofmt y goreleaser (LOW)

`gofmt -w .` sobre los 11 archivos. `.goreleaser.yaml`: añadir bloque `brews:`, corregir `ldflags` (`-ldflags "-X main.version={{.Version}}"`), garantizar `LICENSE` presente. Sin diseño no-trivial.

## File Changes

| File | Action | Descripción |
|------|--------|-------------|
| `internal/cli/confirm.go` | Create | Helper `Confirm` (#1) |
| `internal/cli/middleware.go` | Modify | render+sentinel en `RunHandler` (#4); plug Confirm en path destructivo (#1) |
| `internal/api/client.go` | Modify | DTO Certificate (#2), `Ready()` (#5), mapeo 400 duplicado (#3), borrar ramas 409/404 muertas (#3,#6) |
| `internal/core/cert.go` | Modify | Status desde FechaExpiracion+Activo (#2), empty⇒cert_not_found (#6), MarkRenderable (#4) |
| `internal/core/infra.go` | Modify | Ready real + status "Healthy" (#5), backup pre-upgrade+Confirm (#7), streaming backup (#8), MarkRenderable (#4) |
| `internal/compose/runner.go` | Modify | `RunTo(ctx,w,args)` (#8) |
| `internal/errs/errors.go` | Modify | nuevos codes + tabla exit desambiguada + `Renderable` (#9,#4) |
| `.goreleaser.yaml`, `LICENSE` | Modify/New | brews, ldflags, LICENSE (#11) |
| `**/*_test.go` | Modify | tests al contrato real (#2,#3,#5,#6) |

## Test impact

Tests que afirman el **contrato fabricado** y DEBEN reescribirse al real:
- **Cert DTO/status**: cualquier test que construya `Certificate{Subject,Issuer,ExpiresAt,Estado}` o fixtures con json `subject/issuer/expiresAt/estado` ⇒ usar `id/nombrePropietario/fechaExpiracion/activo/fechaCreacion`. Verificar que el caso "todo expirado" desaparece (era el bug del zero-time).
- **Bootstrap duplicado**: tests que afirman **409**/`CodeConflict` para RUC duplicado ⇒ reescribir a **400** + ProblemDetails `Detail="Ya existe un tenant con el RUC..."` ⇒ `CodeTenantDuplicate`/exit 5. Añadir caso negativo: 400 con RUC inválido ⇒ NO debe mapear a duplicate.
- **Health/Ready**: tests que esperan `status=="ok"` o `serviceTag` ⇒ `"Healthy"`/`"Ready"`; añadir fake para `/health/ready` con caso **503** ⇒ degraded.
- **Cert empty list**: nuevo test `200 []` ⇒ `CodeCertNotFound`/exit 4; eliminar/ajustar test que asume rama 404.
- **RunHandler**: nuevo test que verifica que un sentinel renderable imprime payload a stdout Y error a stderr con exit no-cero.
- **Exit codes**: tests que afirman exit 6 para expiring/expired/timeout/doctor ⇒ 8/9/10/11.

## Migration / Rollout

Sin migración de datos. Repo sin tag v1 (sin consumidores). Cada finding = commit atómico; rollback = revertir el commit. Documentar la tabla de exit codes nueva antes del primer release.

## Open Questions

- [ ] ¿`Issuer` se elimina de la tabla de salida o se deja vacío? (El backend no lo expone; recomendado: quitar columna.)
- [ ] ¿`infra upgrade` exige backup reciente (ventana N min) o siempre crea uno nuevo? (Recomendado: crear uno nuevo, simple y seguro.)
