# Exploración: `sriyactl` como instalador all-in-one + herramienta day-2 (vía Homebrew)

## Objetivo del cambio

Convertir `sriyactl` en el punto de entrada único: instala el stack SriYa, genera `.env`,
levanta los contenedores y deja listo el primer tenant. UX objetivo:

```
brew install JJQuispillo/tap/sriyactl
sriyactl infra install   # checa deps, genera .env, levanta el stack, bootstrap del primer tenant
```

El `install.sh` standalone se DEPRECA y queda como shim delgado (curl|bash) que instala
`sriyactl` y luego ejecuta `sriyactl infra install`.

Decisiones ya tomadas (no re-litigar):
- CLI es el all-in-one (no se mantiene el bash installer en paralelo).
- Org de GitHub = `JJQuispillo` (NO `anomalyco`). `go.mod` y `.goreleaser.yaml` aún dicen `anomalyco`.
- Docker NO es dependencia dura de brew. La CLI detecta + guía; `--auto-install` opcional vía brew.

---

## Estado actual (Current State)

### Lo que hace `install.sh` (bash, en `billing/install.sh`)

Flujo paso a paso (líneas citadas):

1. **Preflight** (`install.sh:44-53`): `command -v docker`; `docker compose version`; `docker info`
   (daemon corriendo). Falla con mensaje accionable si falta cualquiera.
2. **Generación de secretos** (`install.sh:58-62`): `gen_secret()` produce 44 chars `[A-Za-z0-9]`
   leyendo `/dev/urandom` con `tr -dc`. CHARSET-SAFE a propósito: evita `+ / =` de base64 que
   rompen el parseo de connection-strings de Npgsql y los passwords de roles SQL. Sin dependencia de openssl.
3. **Config interactiva opcional** (`install.sh:68-78`): sólo si `[ -t 0 ]` (TTY). Pregunta
   `BILLING_PORT`, `CORS_ORIGIN_0`, `BILLING_DB_USER`. Bajo `curl|bash` el stdin es el script,
   así que se saltan los prompts y usa env vars/defaults. Los secretos NUNCA se preguntan.
4. **Descarga del compose** (`install.sh:81-91`): `mkdir -p $INSTALL_DIR` (default `qora-billing`);
   si ya existe `docker-compose.yml` NO lo toca; si no, baja `docker-compose.prod.yml` desde
   `raw.githubusercontent.com/JJQuispillo/SriYa/$REF` (REF = `v$VERSION`, pin al tag).
5. **Generación de `.env`** (`install.sh:94-126`): si `.env` ya existe NO lo toca. Si no, escribe
   `.env` con `chmod 600` y estas claves:
   - `BILLING_IMAGE_TAG=$VERSION`
   - `BILLING_API_PORT`, `CORS_ORIGIN_0`, `BILLING_DB_USER`
   - `BILLING_DB_PASSWORD`, `BILLING_APP_DB_PASSWORD`, `BILLING_PRIVILEGED_DB_PASSWORD` (3 secretos)
   - `SERVICE_AUTH_TOKEN` (secreto)
   - `ENCRYPTION_KEY` (secreto)
6. **Pull + up** (`install.sh:129-136`): `docker compose pull` (con mensaje de error específico
   si GHCR no es público o el tag no existe), luego `docker compose up -d`.
7. **Espera de health** (`install.sh:139-168`): 45 × 2s = 90s, poll a `http://localhost:$PORT/health`.
   Al estar healthy, lee `SERVICE_AUTH_TOKEN` de `.env` e imprime el comando next-step de bootstrap
   (curl POST `/api/v1/bootstrap` con header `X-Service-Token` y campos `-F`).
8. **Fallo** (`install.sh:170-171`): si no llega a healthy en 90s, sugiere `docker compose logs billing-api`.

### Contrato real del compose (`billing/docker-compose.prod.yml`)

- Servicios: **`billing-api`** y **`billing-db`** (NO `postgres`). Contenedores `qora-billing-api`/`qora-billing-db`.
- DB: usuario **`billing_user`** (default), base **`qora_billing`**, imagen `postgres:16-alpine`.
- Imagen API: `ghcr.io/jjquispillo/sriya:${BILLING_IMAGE_TAG:-latest}`.
- Variables que el compose consume (deben existir en `.env`): `BILLING_IMAGE_TAG`, `BILLING_API_PORT`,
  `BILLING_DB_USER`, `BILLING_DB_PASSWORD`, `BILLING_APP_DB_PASSWORD`, `BILLING_PRIVILEGED_DB_PASSWORD`,
  `ENCRYPTION_KEY`, `SERVICE_AUTH_TOKEN`, `CORS_ORIGIN_0`.
- Healthcheck del compose: `curl -f http://localhost:8080/health`.

### Contrato del endpoint de bootstrap (`billing/src/Qora.Billing.Api/Endpoints/BootstrapEndpoints.cs`)

- Ruta: **`POST /api/v1/bootstrap`** (el grupo es `/api/v1/bootstrap`, el handler mapea `"/"`).
- Auth: SOLO esquema **ServiceToken** → header **`X-Service-Token`** (`BootstrapEndpoints.cs:34-37`).
- Multipart/form-data con: `certificate` (IFormFile .p12/.pfx, máx 10 MB), `ruc`, `razonSocial`,
  `password`, `ownerName` (requeridos); `nombreComercial`, `correoContacto`, `apiKeyName` (opcionales).
- Respuesta **201 Created** con `tenantId` + `apiKey` EN CLARO una sola vez.
- Errores: 400 (campos/cert/RUC inválido o duplicado; transacción revierte), 401 (sin token).
- Confirma el contrato que la CLI ya implementa en `internal/api/client.go:200-268`.

### Lo que ya existe en la CLI Go (`sriyactl/`)

Módulo `github.com/anomalyco/sriyactl`, go 1.23, cobra+viper, keyring, goreleaser. Estructura:
`cmd/sriyactl/`, `internal/{core,config,compose,render,cli,errs,api,secret}`.

Piezas reutilizables YA construidas:
- **`compose.Runner` / `ExecRunner`** (`internal/compose/runner.go`): wrapper seguro de `docker compose`.
  Métodos `Run/Stream/RunTo`, resolución de install dir (`--dir` > `SRIYACTL_HOME` > `$HOME/sriya` >
  `$HOME/qora` > cwd), `ValidateInstallDir` (exige `.env` + `docker-compose.yml`). **Reutilizable para pull/up/ps.**
- **`InfraDoctorHandler`** (`internal/core/infra.go:433-512`): ya checa docker binary (`lookPath`),
  daemon (`compose ps`), install dir, claves de `.env`, longitud de `ENCRYPTION_KEY`, service-token.
  **Reutilizable como el preflight del install.**
- **Health/Ready polling** (`InfraUpgradeHandler` `infra.go:263-293`, `api.Client.Ready`): ya hay un
  loop de espera de `/health/ready` con timeout y poll interval. **Reutilizable para la espera de salud.**
- **Bootstrap del tenant** (`api.Client.BootstrapTenant` + `core.TenantCreate*` + `cli/tenant.go`):
  ya envía el multipart a `/api/v1/bootstrap`, captura el apiKey al keychain, clasifica errores 400/duplicado.
- **`secret.KeyringStore`** (`internal/secret/store.go`): guarda service token (`service_token`) y
  api keys en el keychain; override por env `SRIYACTL_SERVICE_TOKEN` / `SRIYACTL_API_KEY`.
- **`config`** (`internal/config/config.go`): TOML en `~/.config/sriyactl/config.toml`, contextos
  (URL + service_token_ref) y tenants. NO guarda secretos.
- **`.goreleaser.yaml`**: ya tiene builds multi-arch (darwin/linux/windows, amd64/arm64), archives,
  checksums, y un bloque `brews:` apuntando a `homebrew-tap` — pero owner = **`anomalyco`** (a renombrar).

---

## Mapeo: pasos de `install.sh` → Go (qué existe vs qué es net-new)

| Paso install.sh | ¿Existe en Go? | Net-new para `infra install` |
|---|---|---|
| 1. Preflight docker/daemon | SÍ — `InfraDoctorHandler` (docker binary + `compose ps`) | Reusar; mover el check ANTES de exigir install dir (doctor hoy también valida install dir, que aún no existe en install) |
| 2. `gen_secret` 44 chars alnum | NO existe generador de secretos charset-safe en Go | **NET-NEW**: helper en `internal/secret` o nuevo `internal/installer` que produzca `[A-Za-z0-9]` desde `crypto/rand` (NO base64) |
| 3. Config interactiva (port/cors/db user) | NO | **NET-NEW**: prompts TTY-aware (`golang.org/x/term` ya es dep); flags `--port`/`--cors-origin`/`--db-user`; modo no-TTY usa defaults/flags |
| 4. mkdir + descarga compose pin a tag | Parcial: `ExecRunner` resuelve dir pero NO descarga | **NET-NEW**: descargar `docker-compose.prod.yml` desde `raw.githubusercontent.com/JJQuispillo/SriYa/v$VERSION`; no clobber si ya existe |
| 5. Escribir `.env` (no clobber) | Parcial: hay `writeEnvVar/readEnvVar` | **NET-NEW**: renderizar `.env` completo con los 9 claves; `chmod 600`; no clobber |
| 6. `compose pull` + `up -d` | SÍ — `Compose.Run(ctx,"pull")` / `Run(ctx,"up","-d")` | Reusar (idéntico a `InfraUpgradeHandler`) |
| 7. Espera `/health` 90s + next-step | SÍ — loop de `Ready`/`Health` en upgrade | Reusar el patrón; ajustar a `/health` (liveness) o `/health/ready` |
| 8. Mensaje de error de logs | SÍ — `errs` + hints | Reusar |
| (extra) Bootstrap del primer tenant | SÍ — `TenantCreate` + bootstrap API | **Orquestar**: tras health, encadenar el bootstrap. Resolver el chicken-and-egg del service token (ver abajo) |

### Hallazgo crítico (bug latente en el código actual)

`runBackup` (`internal/core/infra.go:331-355`) usa **`exec -T postgres pg_dump -U postgres billing`**,
pero el compose real define servicio **`billing-db`**, usuario **`billing_user`** y base **`qora_billing`**
(confirmado en `docker-compose.prod.yml:50-67` y `README.md:84-90`). Hoy `infra backup`/`infra upgrade`
(que hace backup pre-upgrade) **fallan contra un stack real instalado por install.sh**. Además
`InfraDoctorHandler` (`infra.go:463`) busca la clave `POSTGRES_PASSWORD` en `.env`, pero install.sh
escribe `BILLING_DB_PASSWORD` → el doctor reporta `env-keys: fail` en un stack válido. Estos defaults
deben unificarse como parte del cambio (o convertirse en variables leídas del `.env`/compose).

---

## Bootstrapping de dependencias (Docker)

- **Detección**: ya existe (`lookPath("docker")` + `compose ps` para daemon). Reusar en preflight.
- **Guía vs `--auto-install`**:
  - macOS: Docker Desktop es un **cask con licencia** (no apto como dep dura) → ofrecer **Colima**
    (`brew install colima docker` + `colima start`) como ruta `--auto-install`, o sólo guiar.
  - Linux: guiar a `get-docker.sh` / repos de distro; `--auto-install` es riesgoso (sudo, systemd) →
    recomendar SÓLO guiar en Linux, no auto-instalar.
- **Realista y seguro**: por defecto **detectar + guiar** (mensaje con el comando exacto por OS).
  `--auto-install` opt-in: en macOS instala Colima vía brew; en Linux NO auto-instala (sólo imprime
  instrucciones y sale con código accionable). Nunca correr `sudo` implícito.
- La CLI sigue **dep-light**: no linkea el SDK de Docker, sólo shell-out (ya es la postura de `compose`).

## Distribución por Homebrew — gap actual

`.goreleaser.yaml` ya tiene `brews:` + builds multi-arch, pero falta:
1. **Renombrar owner `anomalyco` → `JJQuispillo`** en `.goreleaser.yaml` (tap.owner, homepage,
   release.github.owner) **y en `go.mod`** (`module github.com/anomalyco/sriyactl`) + todos los imports
   internos (`internal/...` importan `github.com/anomalyco/sriyactl/...` — ~15 archivos).
2. **`sriyactl/` aún NO es repo git** (`../sriyactl` no es git). Hay que `git init`, primer commit, y
   push público a `github.com/JJQuispillo/sriyactl`.
3. **Crear repo `JJQuispillo/homebrew-tap`** (público) para que goreleaser haga commit de la fórmula.
4. **Token**: goreleaser necesita un PAT (`HOMEBREW_TAP_GITHUB_TOKEN` o `GITHUB_TOKEN`) con permiso de
   escritura al tap repo; configurar como secret del workflow.
5. **Release tag-triggered**: falta un workflow `.github/workflows/release.yml` que corra
   `goreleaser release --clean` en `push` de tags `v*`. Hoy `release.draft: true` (queda en borrador).
6. **Formula `test:`** ya valida `sriyactl --version` (soportado por cobra `Version`, ver `main.go` +
   `root.go:22`). OK.

## El shim `install.sh` (resolver el chicken-and-egg)

Rediseño de `curl|bash` para que instale `sriyactl` y luego ejecute `sriyactl infra install`:

1. Detectar OS/arch.
2. **Ruta A (brew presente)**: `brew install JJQuispillo/tap/sriyactl`.
3. **Ruta B (sin brew)**: descargar el binario del release de GitHub (tar.gz por OS/arch desde
   `github.com/JJQuispillo/sriyactl/releases`), verificar checksum (`checksums.txt` ya lo genera
   goreleaser), instalar en `/usr/local/bin` o `~/.local/bin`.
4. `exec sriyactl infra install "$@"` (pasa env vars/flags: VERSION, BILLING_PORT, etc.).

Esto resuelve el chicken-and-egg: el shim sólo bootstrappea el binario; toda la lógica (secretos,
compose, salud, bootstrap) vive una sola vez en Go.

**Segundo chicken-and-egg (más sutil)**: para que `sriyactl tenant create` funcione necesita un
**contexto** en `config.toml` (URL del API) + el **service token en el keychain**. El `infra install`
acaba de generar `SERVICE_AUTH_TOKEN` en `.env`. Por tanto `infra install` debe, además de levantar el
stack, **auto-configurar un contexto local** (p.ej. `local` → `http://localhost:$PORT`) y **sembrar el
service token en el keychain** desde el `.env` recién generado, para que el bootstrap del primer tenant
funcione sin pasos manuales. (Hoy `cli/tenant.go` + `wiring.go:48-70` exigen `current_context` y URL.)

---

## Enfoques (Approaches)

1. **`infra install` monolítico en `core`** — un solo handler que orquesta preflight → secretos →
   download compose → render `.env` → context+keychain seed → pull/up → wait health → bootstrap tenant.
   - Pros: una superficie, fácil de testear con fakes (Compose/API/Secret ya tienen fakes), coherente
     con el patrón handler↔render existente.
   - Cons: handler grande; mezcla I/O de red (download) con compose; hay que extraer subpasos.
   - Esfuerzo: **Medium**.

2. **Nuevo paquete `internal/installer` + comando `infra install` delgado** — el download + render `.env`
   + gen de secretos viven en `installer`; `infra install` compone installer + los handlers existentes
   (doctor, pull/up, wait, tenant bootstrap).
   - Pros: separa lo net-new (descarga/render/secretos) de lo reusable; testeable aislado; mantiene
     `core/infra.go` enfocado en lifecycle.
   - Cons: un paquete más; definir la frontera installer↔compose↔core.
   - Esfuerzo: **Medium**.

3. **Mantener `install.sh` como fuente y sólo añadir `--auto-install` de deps** — descartado: contradice
   la decisión ya tomada (CLI all-in-one). Sólo se menciona para cerrarlo.
   - Esfuerzo: Low pero NO cumple el objetivo.

## Recomendación

**Enfoque 2** (paquete `internal/installer` + comando `infra install` delgado que reusa doctor/compose/
tenant). Razones: aísla lo verdaderamente nuevo (descarga del compose pin-a-tag, render de `.env`,
generador de secretos charset-safe, prompts TTY) y reutiliza al máximo el código ya probado
(`ExecRunner`, `InfraDoctorHandler`, `Ready` polling, `BootstrapTenant`). Encaja con la separación
handler↔render y con los fakes existentes para tests.

Trabajo transversal obligatorio del cambio (independiente del enfoque):
- Renombrar `anomalyco` → `JJQuispillo` en `go.mod`, imports e `.goreleaser.yaml`.
- `git init` + push de `sriyactl/` y crear `homebrew-tap`.
- Workflow de release tag-triggered + token del tap.
- Unificar defaults DB (`billing-db`/`billing_user`/`qora_billing`) y la clave de `.env`
  (`BILLING_DB_PASSWORD` vs `POSTGRES_PASSWORD`) entre el doctor/backup y el compose real (arregla bug latente).
- `infra install` debe sembrar contexto local + service token en keychain para encadenar el bootstrap.
- Reescribir `install.sh` como shim (brew o binario + checksum, luego `exec sriyactl infra install`).

## Riesgos (Risks)

- **Bug latente DB**: `runBackup`/doctor usan `postgres`/`postgres`/`billing` y `POSTGRES_PASSWORD`,
  el stack real usa `billing-db`/`billing_user`/`qora_billing` y `BILLING_DB_PASSWORD`. Hay que arreglarlo
  o el backup pre-upgrade y el doctor fallan en instalaciones reales.
- **Rename `anomalyco`→`JJQuispillo`** toca el module path: rompe imports en ~15 archivos + go.sum; un
  rename incompleto deja el build roto. Verificar `go build ./...` + `go test ./...`.
- **`infra doctor` exige install dir** (vía `compose ps` y `ValidateInstallDir`); en `infra install` el
  dir aún no existe → hay que separar los checks "pre-instalación" (docker/daemon) de los "post" (env keys).
- **Service token en keychain**: sembrar el token desde `.env` al keychain en headless/CI (sin
  secret-service en Linux) puede fallar; necesita fallback a env var `SRIYACTL_SERVICE_TOKEN`.
- **Generador de secretos**: debe ser charset-safe `[A-Za-z0-9]` (igual que bash); usar base64 rompería
  Npgsql. Reproducir exactamente la propiedad de `gen_secret`.
- **Homebrew**: cask vs colima, tap repo inexistente, token, release en `draft:true` (no se publica solo).
- **`curl|bash` shim**: descarga de binario debe verificar checksum (`checksums.txt`); manejar OS/arch
  no soportados (windows/arm64 está `ignore` en goreleaser).
- **Idempotencia / no clobber**: el install debe respetar `.env` y `docker-compose.yml` existentes
  (paridad con install.sh) para no pisar secretos de una instalación previa.

## Listo para Proposal (Ready for Proposal)

**Sí.** Hay suficiente claridad sobre alcance, piezas reutilizables y trabajo net-new. Preguntas abiertas
menores para resolver en proposal/design: (a) nombre exacto del subcomando (`infra install` vs `install`
top-level); (b) si el bootstrap del primer tenant es obligatorio o un flag `--bootstrap` opcional
(requiere `.p12` + password → quizá sólo imprimir el next-step si no se pasan); (c) política de
`--auto-install` por OS; (d) si se unifica el naming DB cambiando el código Go o haciéndolo leer del
`.env`/compose.
