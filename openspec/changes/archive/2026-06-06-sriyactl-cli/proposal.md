# Proposal: `sriyactl` — CLI de operaciones day-2 para el stack SriYa/Qora

## Intent

El self-host de SriYa (microservicio .NET 9 de facturación electrónica SRI, desplegado vía Docker Compose) solo cuenta hoy con `install.sh` (bootstrap `curl | bash`). Operar el stack post-install (upgrades migration-aware, backups, onboarding de tenants, vigilancia de expiración de certificados, emisión de documentos) exige `docker compose` crudo + `curl` manual. Esto es frágil, no auditable y propenso a errores graves: el `apiKey` de bootstrap se muestra **una sola vez** y se pierde con facilidad, y los certificados SRI expiran rompiendo la facturación **en silencio**. `sriyactl` es una herramienta compilada que encapsula estas operaciones con UX segura y **AI-friendly** desde v1.

## Scope

### In Scope (v1 — lo incómodo con `curl` hoy)
- `infra` (wrapper docker compose): `status`, `logs -f`, `upgrade --to <ver>` (migration-aware), `backup`, `doctor` (preflight).
- `tenant`: `create` (= `POST /api/v1/bootstrap` atómico; **auto-captura el apiKey one-time al OS keychain**), `list`, `use <alias>`, `current` (estilo kubectl).
- `cert status <tenant> --warn-days N` (alerta de expiración).
- Baseline transversal AI-friendly: `--output json|yaml|table` (con `schemaVersion`), modo no-TTY auto (`[ -t 0 ]`), exit codes deterministas, error como JSON `{code,message,hint,retryable}`, `--dry-run`/`--yes`/`--no-input`, `SRIYACTL_READONLY=1`.
- Modelo config/seguridad: `~/.config/sriyactl/config.toml` (solo no-secrets), secretos en OS keychain (`go-keyring`) o env. Contextos kubectl-style (host URL + service-token).

### Out of Scope (diferido)
- **v2**: `doc*` (send/list/show/status/void/events/ride), `apikey*`, `cert upload`, `tenant update/usage`, `sriyactl mcp` (MCP server), `sriyactl spec --json`.
- **v3**: generadores `agents-md`/`skill --claude`, pulido de contextos, JSONL en listas, `--field` selection, `sri ping`, `secrets rotate encryption-key`.
- **No** se reemplaza `install.sh` (sigue como bootstrap fino).
- **No** se modifica el backend .NET (CLI = cliente HTTP + wrapper compose).

## Approach

CLI en **Go** (cobra+viper, goreleaser) — decidido por distribución trivial multi-arch + brew tap. Cornerstone: **separación estricta handler ↔ render**: cada comando es un handler tipado que devuelve datos tipados; una capa de presentación renderiza table | json | jsonl | MCP tool-result. Esto hace `--output json`, el MCP server y la tabla casi gratis. Imprimir strings directamente es anti-pattern. Dos modelos de auth: service-token (`X-Service-Token`, admin/tenant/bootstrap) y API key per-tenant (`X-API-Key`, documentos); el CLI elige según comando. Vive en repo hermano `sriyactl/` (módulo Go independiente con su propio pipeline de release).

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `sriyactl/` (repo hermano) | New | Módulo Go nuevo: cmd handlers, render layer, http client, keychain, config |
| `install.sh` | Unmodified | Permanece como bootstrap `curl \| bash` |
| Backend .NET (billing) | Unmodified | Se consume vía API existente (/health, /api/tenants, /api/v1/bootstrap, /api/documents, ...) |
| Goreleaser + brew tap | New | Pipeline de release multi-arch + checksums |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Retrofit de AI-friendly es doloroso después | High | Handler/render + json/non-TTY/exit-codes en el baseline v1 |
| Pérdida del apiKey one-time | High | Auto-captura a OS keychain en `tenant create` |
| Drift entre CLI y API del backend | Med | `spec --json` (v2) + tests contra contrato; versionar `schemaVersion` |
| Agente AI ejecuta acción destructiva (void/revoke) | Med | `SRIYACTL_READONLY=1` / read-only context |
| `upgrade` rompe por migración | Med | Flujo migration-aware: bump tag → pull → up → wait health; `backup` previo |

## Rollback Plan

`sriyactl` es aditivo y vive en repo separado; revertir = no distribuir el binario (sin impacto en el stack ni en `install.sh`). Para operaciones de runtime: `infra upgrade` exige `backup` previo y es reversible vía `restore` + re-pin de `BILLING_IMAGE_TAG` anterior.

## Dependencies

- Backend SriYa con endpoints existentes (README billing).
- Go toolchain, goreleaser, `go-keyring`. Docker/Compose en el host objetivo.

## Success Criteria

- [ ] v1 entrega `infra status/logs/upgrade/backup/doctor`, `tenant create/list/use/current`, `cert status`.
- [ ] Todo comando soporta `--output json` con `schemaVersion` y modo no-TTY automático.
- [ ] `tenant create` captura el apiKey al keychain sin exponerlo en stdout (salvo `--show`).
- [ ] Exit codes deterministas + errores como JSON estructurado.
- [ ] Binarios multi-arch (mac/linux/windows × arm64/x64) vía goreleaser + brew tap.
