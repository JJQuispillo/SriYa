# Delta for infra

## ADDED Requirements

### Requirement: infra install

El comando `sriyactl infra install` MUST ser el punto de entrada de día-1: orquesta preflight de deps, render de `.env`, descarga del compose pin-a-tag, `pull` + `up -d`, espera de salud y (por defecto, en TTY) bootstrap del primer tenant. MUST ser idempotente: una re-ejecución sobre un install dir ya provisto NO destruye secretos ni datos. El preflight de pre-instalación (docker binario + daemon) MUST ejecutarse ANTES de exigir un install dir, de modo distinto a los checks post-instalación (env keys, compose). En `--auto-install` MUST: en macOS ofrecer `brew install colima docker docker-compose` solo si `brew` está presente; en Linux solo guiar (sin `sudo`). El default MUST ser detectar + guiar (auto-install opt-in). MUST honrar el contrato AI: exit code 0 en éxito, código determinista en fallo, y errores como JSON con `code` cuando `--output json`.

#### Scenario: instalación limpia con Docker presente

- GIVEN un host limpio con docker binario + daemon arriba y sin `.env`/`docker-compose.yml`
- WHEN el operador ejecuta `sriyactl infra install`
- THEN MUST generar `.env`, descargar el compose pin-a-tag, hacer `pull` + `up -d` y esperar `/health/ready` OK
- AND MUST terminar con exit code 0 dejando el stack healthy

#### Scenario: re-ejecución idempotente (no-clobber)

- GIVEN un install dir con `.env` y `docker-compose.yml` ya existentes
- WHEN el operador re-ejecuta `sriyactl infra install`
- THEN MUST NOT sobrescribir el `.env` ni el compose existentes (no-clobber estricto)
- AND MUST reconciliar el stack (`up -d`) sin perder secretos ni datos, con exit code 0

#### Scenario: docker daemon ausente en preflight

- GIVEN el binario docker presente pero el daemon detenido y SIN `--auto-install`
- WHEN se ejecuta `sriyactl infra install`
- THEN MUST fallar en el preflight de pre-instalación con `code: docker_unavailable` y un `hint` accionable
- AND MUST NOT crear `.env` ni descargar el compose

#### Scenario: --auto-install en macOS con brew

- GIVEN macOS sin docker pero con `brew` presente y `--auto-install`
- WHEN se ejecuta `sriyactl infra install --auto-install`
- THEN MUST instalar Colima/docker vía `brew install colima docker docker-compose` antes del provisioning

#### Scenario: la salud nunca alcanza ready dentro del timeout

- GIVEN un stack que tras `up -d` no expone `/health/ready` OK dentro de ~90s
- WHEN se ejecuta `sriyactl infra install`
- THEN MUST fallar con `code: install_health_timeout` y exit code distinto de 0, reportando el último estado observado

### Requirement: install genera .env con secretos charset-safe

El render de `.env` durante `infra install` MUST generar secretos desde `crypto/rand` usando un alfabeto `[A-Za-z0-9]` (NUNCA base64), para evitar caracteres que rompen la cadena de conexión de Npgsql. MUST escribir las 9 claves esperadas por el stack, fijar permisos `chmod 600`, y aplicar no-clobber sobre un `.env` existente.

#### Scenario: secretos sin caracteres no-alfanuméricos

- GIVEN un install dir sin `.env`
- WHEN `infra install` genera el `.env`
- THEN cada secreto generado MUST coincidir con `^[A-Za-z0-9]+$` (sin `+`, `/`, `=` ni padding base64)
- AND el archivo `.env` MUST quedar con permisos `600`

#### Scenario: .env existente no se regenera

- GIVEN un `.env` ya presente con secretos previos
- WHEN se ejecuta `infra install`
- THEN MUST conservar el `.env` existente intacto (no-clobber) y NO regenerar secretos

### Requirement: install descarga el compose pin-a-tag

`infra install` MUST descargar `docker-compose.prod.yml` desde `JJQuispillo/SriYa` fijado al tag `v$VERSION` correspondiente a la versión del binario (no `latest`, no rama). MUST aplicar no-clobber sobre un compose existente. Si el archivo remoto no existe para ese tag, MUST fallar con un `code` determinista sin dejar un compose parcial.

#### Scenario: descarga pin-a-tag exitosa

- GIVEN una versión `v1.4.0` del binario y el compose presente en `JJQuispillo/SriYa@v1.4.0`
- WHEN `infra install` descarga el compose
- THEN MUST obtener el `docker-compose.prod.yml` del tag `v1.4.0` y guardarlo como `docker-compose.yml`

#### Scenario: tag remoto sin compose

- GIVEN un tag para el que el compose no existe en el repo remoto
- WHEN `infra install` intenta descargarlo
- THEN MUST fallar con `code: compose_download_failed` y exit code distinto de 0, sin dejar archivo parcial

## MODIFIED Requirements

### Requirement: infra backup

El comando `sriyactl infra backup` MUST generar un dump de la base vía `pg_dump` ejecutado con `docker compose exec` sobre el servicio de Postgres real del stack (`billing-db`), autenticando con el usuario `billing_user` sobre la base `qora_billing`, escribiendo un artefacto con timestamp y reportando su ruta.

(Previamente: `runBackup` usaba `exec -T postgres pg_dump -U postgres billing`, nombres que no existen en el stack real → el backup fallaba.)

#### Scenario: backup exitoso contra el stack real

- GIVEN el container `billing-db` corriendo
- WHEN el operador ejecuta `sriyactl infra backup`
- THEN MUST ejecutar `pg_dump` con servicio `billing-db`, usuario `billing_user` y base `qora_billing`
- AND MUST producir un dump y reportar `path` + `sizeBytes` con exit code 0

#### Scenario: postgres no disponible

- GIVEN el container `billing-db` no está corriendo
- WHEN se ejecuta `sriyactl infra backup`
- THEN MUST fallar con `code: db_unavailable` sin crear un dump parcial

### Requirement: infra doctor

El comando `sriyactl infra doctor` MUST ejecutar checks de preflight: docker presente, daemon arriba, puerto host libre, `.env` con las keys requeridas, imagen GHCR alcanzable, y `ENCRYPTION_KEY` con longitud ≥ 32. La verificación de la contraseña de la base MUST leer la clave `BILLING_DB_PASSWORD` del `.env` (la que escribe el instalador), NO `POSTGRES_PASSWORD`. MUST reportar cada check con su estado y fallar si alguno falla.

(Previamente: el doctor buscaba `POSTGRES_PASSWORD`, ausente del `.env` real → reportaba `env-keys:fail` en un stack válido.)

#### Scenario: todos los checks pasan en stack válido

- GIVEN un host con `.env` que contiene `BILLING_DB_PASSWORD`
- WHEN el operador ejecuta `sriyactl infra doctor`
- THEN el check de env keys MUST leer `BILLING_DB_PASSWORD`, cada check MUST reportar `pass` y el exit code MUST ser 0

#### Scenario: falta BILLING_DB_PASSWORD

- GIVEN un `.env` sin la clave `BILLING_DB_PASSWORD`
- WHEN se ejecuta `sriyactl infra doctor`
- THEN el check de env keys MUST reportar `fail` con un `hint` accionable y exit code distinto de 0
