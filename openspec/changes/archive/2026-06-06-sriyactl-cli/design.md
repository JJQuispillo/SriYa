# Design: `sriyactl` — CLI day-2 ops

## Technical Approach

Módulo Go independiente en repo hermano `sriyactl/`. **Go 1.23+**, `cobra` (comandos) + `viper` (carga de config), `goreleaser` (release multi-arch). El núcleo es la **separación estricta handler ↔ render**: la capa CLI solo parsea flags y delega; los handlers tipados producen datos puros sin I/O de presentación; la capa render proyecta esos datos a table | json | yaml | (v2) MCP tool-result. Esto hace `--output json`, no-TTY y el MCP server casi gratis, y mantiene v1 forward-compatible con v2/v3.

## Architecture Decisions

| Decisión | Elegido | Rechazado | Rationale |
|----------|---------|-----------|-----------|
| Separación handler/render | handler tipado `func(ctx, In)(Out,error)` + render genérico | `fmt.Println` en comando | Único punto donde nace JSON/MCP; testeable sin I/O |
| Compose ops | shell-out a `docker compose` (`os/exec`) | Docker SDK Go | Paridad 1:1 con install.sh, sin acoplar a versión de API del daemon, stream nativo de logs |
| Config | `viper` + TOML | hand-rolled | Precedencia flag>env, contextos kubectl-style, merge gratis |
| Secretos | `github.com/zalando/go-keyring` | escribir en TOML | Keychain nativo macOS/Linux(secret-service)/Windows; env fallback para CI/headless |
| HTTP | stdlib `net/http` | resty/etc | Cero deps, control total de headers de auth |
| MCP (v2) | `modelcontextprotocol/go-sdk` (oficial) | mark3labs/mcp-go | SDK oficial sigue el spec; los handlers ya existen, solo se envuelven |
| TTY detection | `golang.org/x/term.IsTerminal(int(os.Stdout.Fd()))` | leer `$TERM` | Robusto y portable |

## Cornerstone: handler ↔ render

Cada comando expone un handler puro. El resultado se envuelve en `Output[T]` con `schemaVersion`. La capa render decide formato según `--output` (auto: TTY→table, no-TTY→json).

```go
// internal/core
type Output[T any] struct {
    SchemaVersion string `json:"schemaVersion"`
    Kind          string `json:"kind"` // "TenantList", "InfraStatus", ...
    Data          T      `json:"data"`
}
type Handler[In, Out any] func(ctx context.Context, in In) (Output[Out], error)

// internal/render
type Format int // Table | JSON | YAML | MCP
type Renderable interface { Columns() []string; Rows() [][]string } // para table
func Render[T any](w io.Writer, out Output[T], f Format) error
```

`cli` (cobra) → parsea flags → llama `core.Handler` → pasa `Output[T]` a `render.Render`. El **mismo** handler lo invoca `internal/mcp` (v2) devolviendo `Output[T]` como MCP tool-result JSON. Imprimir strings dentro de un handler es anti-pattern prohibido.

## Project Layout

```
sriyactl/
  cmd/sriyactl/main.go        # bootstrap cobra root
  internal/cli/               # comandos cobra: thin (flags→core→render)
  internal/core/              # handlers tipados (lógica real, sin I/O present.)
  internal/api/               # HTTP client del backend SriYa (interface Client)
  internal/compose/           # wrapper docker compose (interface Runner, os/exec)
  internal/config/            # config.toml + contextos kubectl-style (viper)
  internal/secret/            # keychain (go-keyring) + env fallback (interface Store)
  internal/render/            # table | json | yaml (+ mcp en v2)
  internal/mcp/               # (v2) MCP server sobre core
  internal/errs/              # CLIError {code,message,hint,retryable} + exit map
  .goreleaser.yaml
  go.mod
```

## Config & Secret Model

`~/.config/sriyactl/config.toml` (solo no-secrets):

```toml
current_context = "prod"
current_tenant  = "acme"
[contexts.prod]      = { url = "https://sri.example.com", service_token_ref = "keychain" }
[tenants.acme]       = { id = "uuid", ruc = "...", env = "prod" }
```

Secretos **nunca** en TOML. Claves keychain: `sriyactl/<context>` → service-token; `sriyactl/<context>/<tenant>` → api-key. Env override: `SRIYACTL_SERVICE_TOKEN`, `SRIYACTL_API_KEY`.

**Precedencia**: `flag > env > keychain > config`.

## Auth Dispatch

Cada comando declara su auth en metadata:

```go
type AuthKind int // ServiceToken | TenantAPIKey
```

El `api.Client` resuelve la credencial vía precedencia y setea el header: `ServiceToken` → `X-Service-Token` (+ `X-Tenant-Id` per-call, scoped por tenant: cert/tenant/lifecycle/admin); `TenantAPIKey` → `X-API-Key` (documentos v2). Un solo punto de inyección en el RoundTripper.

> **`X-Tenant-Id` es per-call**, NO por-contexto. El `api.RoundTripper` acepta `TenantID string` por invocación:
> - **omitido** para `bootstrap` (el tenant no existe aún) y `health` (anónimo).
> - **presente** para `cert` / `tenant` (get-by-id) / `lifecycle`, resuelto por el handler desde el contexto activo o un override `--tenant <alias>`.
>
> Esto se modela como un `func() (tenantID string, ok bool)` o un valor explícito en el request de cada método. Ver task 3.2 para el wiring exacto.

## Output Envelope & Errors

```json
{ "schemaVersion": "1.0", "kind": "TenantList", "data": [...] }
{ "error": { "code": "TENANT_NOT_FOUND", "message": "...", "hint": "...", "retryable": false } }
```

**Exit codes**: 0 ok · 1 generic · 2 usage/flags · 3 auth · 4 not-found · 5 conflict · 6 network/retryable · 7 readonly-blocked. `errs.CLIError` mapea code→exit.

**Auto-non-TTY**: `--output` ausente → table si stdout es TTY, else json. `--dry-run` retorna un `Plan` object (acciones que ejecutaría) sin mutar. **`SRIYACTL_READONLY=1`** se valida en un único gate (`core.guardMutation`) antes de cualquier handler mutador → exit 7.

## Infra / Compose Wrapper

`compose.Runner` ejecuta desde el install dir (descubre `.env` + `docker-compose.yml`; override `--dir` / `SRIYACTL_HOME`). `status`/`backup`/`upgrade` capturan stdout/stderr; `logs -f` hace stream directo. `upgrade --to`: backup → bump `BILLING_IMAGE_TAG` → pull → up -d → wait `/health`. Reversible via `restore` + re-pin de tag previo.

## Interfaces / Contracts

```go
type api.Client interface {
    Health(ctx) (Health, error)
    BootstrapTenant(ctx, BootstrapReq) (BootstrapResp, error) // POST /api/v1/bootstrap (multipart/form-data)
    CertStatus(ctx, tenantID string) (Cert, error)            // GET  /api/v1/certificates (+X-Tenant-Id)
}
type compose.Runner interface { Run(ctx, args ...string) (Result, error); Stream(ctx, w io.Writer, args ...string) error }
type secret.Store interface { Get(key string) (string, error); Set(key, val string) error }
type tenants.Store interface {                          // LOCAL read of ~/.config/sriyactl/config.toml
    ListKnown(ctx) ([]TenantRef, error)                  // tenants registered in the active context
    Upsert(ctx, t TenantRef) error
    Get(ctx, alias string) (TenantRef, error)
}
```

Las cuatro interfaces se inyectan en los handlers → mockeables sin red ni Docker.

> **`ListTenants` is a local read, NOT a backend call.** There is no `GET /api/v1/tenants` (list) endpoint in the backend (OQ 3.3 resolution: only `GET /api/v1/tenants/{id:guid}` exists). The CLI's `tenant list` command is implemented as a read of `~/.config/sriyactl/config.toml` via the `tenants.Store` interface in `internal/config`, returning the tenants registered in the **active context**. This keeps `api.Client` strictly HTTP-only and matches the kubectl model (the CLI's local view of tenants it knows about, not a global admin list).

## Testing Strategy

| Layer | Qué | Cómo |
|-------|-----|------|
| Unit | handlers `core` | mock `api.Client` / `compose.Runner` / `secret.Store`; sin red |
| Golden | render table/json/yaml | golden-file por `kind`; `-update` regenera |
| Integration | wiring cli→core→render, precedencia auth | client HTTP fake (`httptest`) |

`go test ./...` (config) corre todo. `go build ./...` valida cross-compile básico; goreleaser CI cubre matriz multi-arch.

## Phasing Hooks

- **v2 MCP**: `internal/mcp` envuelve los `core.Handler` existentes → `Output[T]` ya es serializable como tool-result. `spec --json` emite el catálogo de handlers/kinds.
- **v2 doc/apikey**: nuevos handlers en `core` + métodos en `api.Client`; auth `TenantAPIKey` ya soportado.
- **v3 JSONL/`--field`**: nuevo `Format` en render + proyección; sin tocar handlers. Generadores (`agents-md`, `skill`) leen el mismo catálogo de `spec --json`.

## Known Limitations (v1)

- **`tenant_duplicate` mapping is heuristic.** The backend does not currently return a stable machine-readable `code` field in its error envelope. The CLI v1 maps `400` + presence of `"RUC"`/`"duplicad"` in the response body to `tenant_duplicate`. Acceptable for v1; accurate but fragile. **Follow-up**: add a stable `code` field to the backend error envelope (`tenant_duplicate`, `cert_invalid`, `password_mismatch`, …) so v2 can drop the heuristic. Tracked in task 8.6 (backend follow-up — out of scope for this change).

## Migration / Rollout

Aditivo, repo separado. Rollback = no distribuir binario. Sin migración de datos.

## Open Questions

- [ ] Confirmar nombre exacto del endpoint bootstrap (`/api/v1/bootstrap` vs flujo tenant+apikey) contra README billing en apply.
- [ ] Versión MCP go-sdk a fijar en v2 (no bloquea v1).
