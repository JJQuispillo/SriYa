# infra Specification (sriyactl v1)

## Purpose

Comandos `infra` de `sriyactl`: wrapper seguro sobre `docker compose` + endpoints `/health` para operar el stack SriYa post-install. Todos operan desde el **install dir** que contiene `.env` + `docker-compose.yml`; si falta cualquiera, el comando MUST fallar con error claro (`code: install_dir_invalid`) antes de actuar.

## Requirements

### Requirement: infra status

El comando `sriyactl infra status` MUST agregar el estado del stack: salida de `docker compose ps`, `GET /health`, `GET /health/ready` y el image tag resuelto desde `BILLING_IMAGE_TAG` en `.env`. MUST devolver datos tipados (no strings) para renderizarse como table/json/yaml.

#### Scenario: stack sano

- GIVEN el stack está levantado y los endpoints responden 200
- WHEN el operador ejecuta `sriyactl infra status`
- THEN MUST mostrar containers (name/state), `health=ok`, `ready=ok` y el `imageTag` resuelto
- AND el exit code MUST ser 0

#### Scenario: backend no responde

- GIVEN los containers existen pero `GET /health` no responde o devuelve no-2xx
- WHEN el operador ejecuta `sriyactl infra status`
- THEN MUST marcar `health=down` y reportar el resto del estado disponible
- AND el exit code MUST ser distinto de 0 (degraded)

### Requirement: infra logs

El comando `sriyactl infra logs [-f]` MUST transmitir los logs de los servicios del stack vía `docker compose logs`, soportando seguimiento continuo con `-f`.

#### Scenario: seguimiento de logs

- GIVEN el stack está corriendo
- WHEN el operador ejecuta `sriyactl infra logs -f`
- THEN MUST transmitir logs en tiempo real hasta interrupción (SIGINT) con exit code 0

### Requirement: infra upgrade

El comando `sriyactl infra upgrade --to <ver>` MUST ser migration-aware: actualizar `BILLING_IMAGE_TAG` en `.env`, luego `pull` + `up -d` y esperar a que `/health/ready` esté OK (wait-health). MUST advertir realizar `backup` previo cuando la versión implica migraciones. Si la salud nunca se recupera dentro del timeout, MUST hacer rollback del tag anterior en `.env` y fallar.

#### Scenario: upgrade exitoso

- GIVEN el stack sano en versión actual y `--to v1.4.0`
- WHEN el operador ejecuta `sriyactl infra upgrade --to v1.4.0`
- THEN MUST escribir `BILLING_IMAGE_TAG=v1.4.0`, hacer pull+up y esperar `ready=ok`
- AND el exit code MUST ser 0

#### Scenario: la salud nunca se recupera

- GIVEN un upgrade donde `/health/ready` no alcanza OK dentro del timeout
- WHEN se ejecuta `sriyactl infra upgrade --to <ver>`
- THEN MUST restaurar el `BILLING_IMAGE_TAG` anterior en `.env`
- AND MUST fallar con `code: upgrade_health_timeout` y exit code distinto de 0

### Requirement: infra backup

El comando `sriyactl infra backup` MUST generar un dump de la base vía `pg_dump` ejecutado con `docker compose exec` sobre el servicio de Postgres, escribiendo un artefacto con timestamp y reportando su ruta.

#### Scenario: backup exitoso

- GIVEN el container de Postgres está corriendo
- WHEN el operador ejecuta `sriyactl infra backup`
- THEN MUST producir un archivo de dump y reportar `path` + `sizeBytes` con exit code 0

#### Scenario: postgres no disponible

- GIVEN el container de Postgres no está corriendo
- WHEN se ejecuta `sriyactl infra backup`
- THEN MUST fallar con `code: db_unavailable` sin crear un dump parcial

### Requirement: infra restore

El comando `sriyactl infra restore <file>` MUST restaurar un dump a la base. Es destructivo: MUST pedir confirmación interactiva, honrar `--yes`/`--no-input` para saltarla, y `--dry-run` MUST reportar las acciones planificadas sin modificar la base.

#### Scenario: restore con dry-run

- GIVEN un archivo de dump válido
- WHEN el operador ejecuta `sriyactl infra restore dump.sql --dry-run`
- THEN MUST reportar las acciones planificadas como datos y NO modificar la base, con exit code 0

#### Scenario: restore confirmado

- GIVEN un archivo de dump válido y `--yes`
- WHEN el operador ejecuta `sriyactl infra restore dump.sql --yes`
- THEN MUST restaurar el dump y reportar éxito con exit code 0

### Requirement: infra doctor

El comando `sriyactl infra doctor` MUST ejecutar checks de preflight: docker presente, daemon arriba, puerto host libre, `.env` con las keys requeridas, imagen GHCR alcanzable, y `ENCRYPTION_KEY` con longitud ≥ 32. MUST reportar cada check con su estado y fallar si alguno falla.

#### Scenario: todos los checks pasan

- GIVEN un host correctamente configurado
- WHEN el operador ejecuta `sriyactl infra doctor`
- THEN cada check MUST reportar `pass` y el exit code MUST ser 0

#### Scenario: un check falla

- GIVEN un host donde `ENCRYPTION_KEY` tiene menos de 32 caracteres
- WHEN se ejecuta `sriyactl infra doctor`
- THEN el check de `ENCRYPTION_KEY` MUST reportar `fail` con un `hint` accionable
- AND el exit code MUST ser distinto de 0

## Out of scope (v2+)

Defer a iteraciones posteriores: `infra restart/down` granular, rotación de `encryption-key` (v3), y dumps incrementales/programados.
