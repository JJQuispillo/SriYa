# Delta for infra (sriyactl v1 fixes)

Corrige confirmación de ops destructivas, readiness real, y backup pre-upgrade.
Backend verificado: `src/Qora.Billing.Api/Endpoints/HealthEndpoints.cs:20-49`
(`GET /health` liveness = `{status:"Healthy"}`; `GET /health/ready` readiness con
`Database.CanConnectAsync` → `200 {status:"Ready"}` ó `503` si la BD no conecta — endpoints distintos).

## MODIFIED Requirements

### Requirement: infra restore

El comando `sriyactl infra restore <file>` es destructivo y MUST aplicar un gate de
confirmación ANTES de cualquier side-effect: (a) cuando es interactivo (TTY) y no se pasó
`--yes`/`--no-input`, MUST pedir confirmación y abortar sin efecto si el usuario no confirma;
(b) cuando se pasó `--yes` o `--no-input`, MUST proceder sin prompt; (c) cuando es no-interactivo
(non-TTY) y NO se pasó `--yes`/`--no-input`, MUST rehusar con un error claro
(`code: confirmation_required`) y exit code distinto de 0, SIN ejecutar la restauración.
Los flags `--yes`/`--no-input` (declarados en `middleware.go`/`root.go`) MUST leerse realmente.
`--dry-run` MUST reportar las acciones planificadas como datos sin modificar la base.

(Previamente: `restore` ejecutaba sin leer `--yes`/`--no-input` y sin prompt TTY alguno.)

#### Scenario: restore interactivo sin --yes pide confirmación

- GIVEN stdin/stdout es TTY, un dump válido y SIN `--yes`/`--no-input`
- WHEN el operador ejecuta `sriyactl infra restore dump.sql` y NO confirma
- THEN MUST abortar antes del side-effect sin modificar la base, con exit code distinto de 0

#### Scenario: restore con --yes procede sin prompt

- GIVEN un dump válido y `--yes` (o `--no-input`)
- WHEN el operador ejecuta `sriyactl infra restore dump.sql --yes`
- THEN MUST restaurar el dump sin pedir confirmación y reportar éxito con exit code 0

#### Scenario: restore no-interactivo sin --yes rehúsa

- GIVEN stdout no es TTY y NO se pasó `--yes`/`--no-input`
- WHEN se ejecuta `sriyactl infra restore dump.sql`
- THEN MUST rehusar con `code: confirmation_required` y exit code distinto de 0, SIN tocar la base

#### Scenario: restore con dry-run no produce efectos

- GIVEN un dump válido y `--dry-run`
- WHEN se ejecuta `sriyactl infra restore dump.sql --dry-run`
- THEN MUST reportar las acciones planificadas como datos y NO modificar la base, con exit code 0

### Requirement: infra status

El comando `sriyactl infra status` MUST consultar `GET /health` (liveness) y, por separado,
`GET /health/ready` (readiness, distinto endpoint que valida la BD) — NO llamar `/health` dos
veces. MUST reportar `health` (liveness) y `ready` (readiness) como campos distintos junto a
los containers y el `imageTag`. Cuando la readiness está degradada (`/health/ready` devuelve
`503` o no responde), MUST reflejarlo en la salida (`ready=down`) y emitir el payload de estado
disponible Y señalizar vía stderr + exit code distinto de 0 (sin descartar el payload).

(Previamente: `status` fabricaba readiness llamando `/health` dos veces; nunca pegaba `/health/ready`.)

#### Scenario: stack sano (liveness y readiness OK)

- GIVEN `GET /health` → `200 {status:"Healthy"}` y `GET /health/ready` → `200 {status:"Ready"}`
- WHEN el operador ejecuta `sriyactl infra status`
- THEN MUST mostrar containers, `health=ok`, `ready=ok` y el `imageTag` resuelto, con exit code 0

#### Scenario: readiness degradada (DB no conecta)

- GIVEN `GET /health` → `200` pero `GET /health/ready` → `503`
- WHEN el operador ejecuta `sriyactl infra status`
- THEN MUST reportar `health=ok` y `ready=down` distinguibles, emitiendo el payload de estado
- AND MUST señalizar degradación vía stderr + exit code distinto de 0 (payload NO descartado)

### Requirement: infra upgrade

El comando `sriyactl infra upgrade --to <ver>` MUST tomar (o exigir un backup reciente) ANTES
de mutar `.env` o hacer `pull`: el orden mandatorio es backup → bump `BILLING_IMAGE_TAG` →
`pull` → `up -d` → esperar `GET /health/ready` OK. Si la salud no se recupera dentro del timeout,
MUST hacer rollback del tag anterior y fallar con `code: upgrade_health_timeout`. Si no logra
respaldar ni hay backup reciente, MUST abortar antes de mutar `.env`.

(Previamente: `upgrade` nunca hacía el backup pre-upgrade mandatorio ni avisaba.)

#### Scenario: upgrade respalda antes de mutar

- GIVEN el stack sano y `--to v1.4.0`
- WHEN el operador ejecuta `sriyactl infra upgrade --to v1.4.0`
- THEN MUST producir (o validar) un backup ANTES de escribir `BILLING_IMAGE_TAG` y hacer pull
- AND luego esperar `ready=ok` vía `/health/ready` y terminar con exit code 0

#### Scenario: la salud nunca se recupera → rollback + timeout

- GIVEN un upgrade donde `/health/ready` no alcanza OK dentro del timeout
- WHEN se ejecuta `sriyactl infra upgrade --to <ver>`
- THEN MUST restaurar el `BILLING_IMAGE_TAG` anterior y fallar con `code: upgrade_health_timeout` y exit distinto de 0
