# Proposal: `sriyactl` instalador all-in-one + distribución Homebrew

## Intent

Hoy la instalación de SriYa vive en `install.sh` (bash) mientras `sriyactl` (Go) sólo hace day-2 ops. Duplica lógica y deja a la CLI sin valor de día-1. Objetivo: hacer de `sriyactl` el punto de entrada único —`brew install JJQuispillo/tap/sriyactl` + `sriyactl infra install`— que checa deps, genera `.env`, levanta el stack y deja listo el primer tenant. El bash se deprecа a un shim delgado.

## Scope

### In Scope
- Nuevo paquete `internal/installer` + comando `sriyactl infra install` que compone preflight (doctor), descarga del compose pin-a-tag, render `.env`, pull/up, espera de salud y bootstrap del tenant.
- Generador de secretos charset-safe `[A-Za-z0-9]` desde `crypto/rand` (NO base64).
- Render de `.env` (9 claves) con `chmod 600` y **no-clobber**; descarga de `docker-compose.prod.yml` desde `JJQuispillo/SriYa@v$VERSION` (no-clobber).
- Prompts TTY (`--no-bootstrap` y flags para headless); bootstrap interactivo por defecto en TTY.
- Auto-config de contexto local + siembra del service token en keychain (resuelve el 2º chicken-and-egg, F5).
- Política `--auto-install`: macOS → ofrecer `brew install colima docker docker-compose` sólo si brew presente; Linux → guiar, sin `sudo`. Default = detectar+guiar.
- **Fix bugs latentes**: `runBackup` y `InfraDoctorHandler` deben usar `billing-db`/`billing_user`/`qora_billing` y la clave `BILLING_DB_PASSWORD` (F1, F2). Separar checks pre-instalación de post (F6).
- Rename `anomalyco`→`JJQuispillo` en `go.mod`, ~15 imports e `.goreleaser.yaml` (F3).
- `install.sh` reescrito a shim (brew o binario + checksum, luego `exec sriyactl infra install`).
- Distribución: `git init` + push público de `sriyactl`, repo `JJQuispillo/homebrew-tap`, PAT del tap, workflow release tag-triggered, flip `release.draft` (F4).

### Out of Scope
- Cualquier cosa más allá del instalador all-in-one + distribución brew.
- Soporte Windows/arm64 (queda `ignore` en goreleaser).
- Auto-instalación de Docker en Linux (sólo guía).
- Migración de datos / upgrades de esquema fuera del flujo de install.
- Reescribir el contrato del endpoint de bootstrap (ya verificado).

## Approach

**Enfoque 2** (recomendado en exploración): paquete `internal/installer` aísla lo net-new (descarga pin-a-tag, render `.env`, gen de secretos charset-safe, prompts TTY); `infra install` lo orquesta reusando `ExecRunner`, `InfraDoctorHandler`, el polling de `/health/ready` y `BootstrapTenant`. Mantiene la separación handler↔render y los fakes existentes para tests.

## Affected Areas

| Área | Impacto | Descripción |
|------|---------|-------------|
| `sriyactl/internal/installer/` | New | Gen secretos, render `.env`, descarga compose, prompts TTY |
| `sriyactl/internal/cli/infra.go` (o `core/infra.go`) | Modified | Comando `infra install`; separar preflight pre/post |
| `sriyactl/internal/core/infra.go` | Modified | Fix `runBackup` (F1) y `InfraDoctorHandler` env key (F2) |
| `sriyactl/internal/cli/wiring.go` | Modified | Siembra contexto local + service token (F5) |
| `sriyactl/go.mod` + ~15 imports | Modified | Rename `anomalyco`→`JJQuispillo` (F3) |
| `sriyactl/.goreleaser.yaml` | Modified | owner/homepage/tap; flip `release.draft` (F3, F4) |
| `sriyactl/.github/workflows/release.yml` | New | `goreleaser release --clean` en tags `v*` (F4) |
| `billing/install.sh` | Modified | Reescrito a shim delgado |
| `JJQuispillo/homebrew-tap` (repo) | New | Tap público para la fórmula (F4) |

## Risks

| Riesgo | Probabilidad | Mitigación |
|--------|-------------|------------|
| Rename incompleto deja el build roto (F3) | Med | `go build ./... && go test ./...` en CI antes de release |
| Bug DB no arreglado → backup/doctor fallan en stack real (F1, F2) | High | Incluido en scope; tests con compose real / fakes |
| Secretos base64 rompen Npgsql | Med | Generador `[A-Za-z0-9]` reproduciendo `gen_secret` del bash |
| Siembra de token en keychain falla headless/Linux sin secret-service (F5) | Med | Fallback a env `SRIYACTL_SERVICE_TOKEN` |
| Clobber de `.env`/`docker-compose.yml` existentes pisa secretos | Med | No-clobber estricto (paridad con install.sh) |
| Homebrew: cask vs colima, tap inexistente, PAT, `draft:true` | Med | Crear tap+PAT; flip draft; usar Colima no-cask |
| Shim descarga binario sin verificar → corrupción/OS no soportado | Med | Verificar `checksums.txt`; salir con guía en OS/arch no soportados |

## Rollback Plan

- CLI: el rename y el nuevo comando viven en commits aislados; revertir el merge restaura el module path `anomalyco` y elimina `infra install`. El binario previo sigue en releases.
- `install.sh`: conservar el bash original en git history; revertir el shim restaura el instalador standalone funcional.
- Distribución: el workflow es tag-triggered; no publicar el tag (o despublicar el release/fórmula del tap) detiene la distribución sin tocar el código.
- Fixes DB (F1/F2): cambios localizados y reversibles por commit.

## Dependencies

- Repo `sriyactl` debe poder pushearse público a `github.com/JJQuispillo/sriyactl`.
- PAT con escritura a `JJQuispillo/homebrew-tap`.
- `docker-compose.prod.yml` debe existir en `JJQuispillo/SriYa` en cada tag `v$VERSION`.
- Endpoint `POST /api/v1/bootstrap` (contrato ya verificado).

## Success Criteria

- [ ] `brew install JJQuispillo/tap/sriyactl` instala el binario; `sriyactl --version` responde.
- [ ] `sriyactl infra install` en un host limpio (con Docker) deja el stack healthy y un tenant bootstrapeado.
- [ ] `infra doctor` y `infra backup` funcionan contra un stack real (F1/F2 resueltos).
- [ ] `go build ./... && go test ./...` verdes tras el rename (F3).
- [ ] `install.sh` (shim) instala sriyactl y ejecuta `infra install` end-to-end.
- [ ] Push de un tag `v*` dispara el release y publica la fórmula en el tap (no draft).
- [ ] `.env`/`docker-compose.yml` existentes NO se sobrescriben (no-clobber).

## Open Questions (remaining)

- ¿Unificar el naming DB cambiando el código Go (hardcode a `billing-db`/`billing_user`/`qora_billing`) o leyéndolo del `.env`/compose? — a resolver en design.
- ¿`infra install` debe correr `colima start` tras instalar Colima, o sólo instalar y guiar? — design.
