# Delta for tenant

## ADDED Requirements

### Requirement: bootstrap del primer tenant encadenado desde install

Tras dejar el stack healthy, `sriyactl infra install` MUST encadenar el bootstrap del primer tenant. El comportamiento por defecto MUST ser interactivo cuando stdin/stdout es TTY (recolectar RUC, razón social, certificado, etc. vía prompts), y MUST poder ejecutarse headless cuando se proveen los flags requeridos. Un flag `--no-bootstrap` MUST saltar este paso dejando el stack provisto pero sin tenant. El bootstrap MUST reusar el contrato verificado `POST /api/v1/bootstrap` con `X-Service-Token`. Si el bootstrap falla, MUST NOT dejar el stack a medias sin señal: MUST reportar el `code` del fallo con exit distinto de 0, conservando el stack ya levantado.

#### Scenario: bootstrap interactivo por defecto en TTY

- GIVEN stdin/stdout es TTY y el stack quedó healthy tras `infra install`
- WHEN finaliza el provisioning sin `--no-bootstrap`
- THEN MUST prompt-ear por los datos del primer tenant y llamar `POST /api/v1/bootstrap` con `X-Service-Token`
- AND MUST registrar el contexto/alias y devolver `tenantId` con exit code 0

#### Scenario: --no-bootstrap salta el tenant

- GIVEN `infra install --no-bootstrap`
- WHEN el stack queda healthy
- THEN MUST NOT llamar el endpoint de bootstrap ni crear tenant alguno
- AND MUST terminar con exit code 0 informando que no se creó tenant

#### Scenario: headless con flags

- GIVEN un entorno non-TTY y todos los flags requeridos (`--ruc`, `--razon-social`, `--cert`, etc.)
- WHEN se ejecuta `infra install` headless
- THEN MUST hacer el bootstrap sin prompts usando los flags
- AND si falta un flag requerido MUST fallar con `code: bootstrap_input_required` sin colgarse esperando input

### Requirement: install siembra contexto local + service token

`infra install` MUST resolver el segundo chicken-and-egg sembrando, ANTES del bootstrap, un contexto local (host URL del stack recién levantado) en la config y el service token derivado del `.env` recién generado. El service token MUST sembrarse en el OS keychain; cuando el keychain no está disponible (headless/Linux sin secret-service), MUST hacer fallback a la variable de entorno `SRIYACTL_SERVICE_TOKEN` sin abortar el flujo.

#### Scenario: siembra de contexto + token en keychain

- GIVEN un host con OS keychain disponible tras generar el `.env`
- WHEN `infra install` prepara el bootstrap
- THEN MUST registrar un contexto local apuntando al stack y guardar el service token en el keychain
- AND el subsiguiente `tenant create` MUST autenticar con ese contexto sin pedir credenciales

#### Scenario: keychain no disponible → fallback a env var

- GIVEN un host Linux sin secret-service
- WHEN `infra install` intenta sembrar el service token
- THEN MUST hacer fallback a `SRIYACTL_SERVICE_TOKEN` y continuar el flujo sin abortar
- AND MUST informar el modo de fallback usado
