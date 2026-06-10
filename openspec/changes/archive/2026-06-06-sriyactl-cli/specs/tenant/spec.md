# tenant Specification (sriyactl v1)

## Purpose

Comandos `tenant` de `sriyactl`: onboarding atómico y modelo de tenant activo estilo kubectl. Todos usan auth **service-token** (`X-Service-Token`) contra el host del **contexto activo**. Los tenants viven dentro de un contexto (host URL + service-token) en `~/.config/sriyactl/config.toml` (solo no-secrets); el `apiKey` per-tenant vive en el OS keychain.

## Requirements

### Requirement: tenant create

El comando `sriyactl tenant create` MUST hacer onboarding atómico vía `POST /api/v1/bootstrap` con header `X-Service-Token`. La respuesta devuelve `tenantId` + `apiKey` **una sola vez**; el CLI MUST auto-capturar el `apiKey` en el OS keychain y registrar un alias local en el contexto activo. MUST NOT imprimir el `apiKey` en stdout salvo que se pase `--show`.

#### Scenario: onboarding exitoso

- GIVEN un contexto activo con service-token válido y datos de tenant nuevos
- WHEN el operador ejecuta `sriyactl tenant create --ruc <ruc> --alias acme`
- THEN MUST llamar `POST /api/v1/bootstrap` con `X-Service-Token`
- AND MUST guardar el `apiKey` en el keychain bajo el alias `acme` y registrar el alias en config
- AND MUST devolver `tenantId` sin exponer el `apiKey` en stdout, con exit code 0

#### Scenario: RUC duplicado

- GIVEN un tenant ya existe para ese RUC
- WHEN se ejecuta `sriyactl tenant create --ruc <ruc>`
- THEN el backend MUST responder conflicto y el CLI MUST fallar con `code: tenant_duplicate`
- AND MUST NOT registrar alias ni escribir en el keychain, con exit code distinto de 0

#### Scenario: mostrar el apiKey explícitamente

- GIVEN `--show`
- WHEN el onboarding tiene éxito
- THEN MUST incluir el `apiKey` en la salida además de guardarlo en el keychain

### Requirement: tenant list

El comando `sriyactl tenant list` MUST listar los tenants conocidos del contexto activo (alias + tenantId), marcando cuál es el activo. MUST devolver datos tipados.

#### Scenario: listar tenants

- GIVEN un contexto con dos tenants registrados, uno activo
- WHEN el operador ejecuta `sriyactl tenant list`
- THEN MUST listar ambos con su alias/tenantId y marcar el activo con exit code 0

### Requirement: tenant use

El comando `sriyactl tenant use <alias>` MUST fijar el tenant activo (scoped al contexto activo) persistiéndolo en config. Un `--tenant <alias>` ad-hoc en cualquier comando MUST sobrescribir el activo solo para esa invocación sin persistir.

#### Scenario: fijar tenant activo

- GIVEN el alias `acme` registrado en el contexto activo
- WHEN el operador ejecuta `sriyactl tenant use acme`
- THEN MUST persistir `acme` como tenant activo del contexto con exit code 0

#### Scenario: alias inexistente

- GIVEN un alias no registrado
- WHEN se ejecuta `sriyactl tenant use ghost`
- THEN MUST fallar con `code: tenant_not_found` y NO cambiar el tenant activo

#### Scenario: override ad-hoc

- GIVEN `acme` es el tenant activo y `beta` también está registrado
- WHEN se ejecuta cualquier comando con `--tenant beta`
- THEN ese comando MUST operar sobre `beta` sin cambiar el activo persistido

### Requirement: tenant current

El comando `sriyactl tenant current` MUST mostrar el tenant activo del contexto actual (alias + tenantId).

#### Scenario: hay tenant activo

- GIVEN `acme` es el tenant activo
- WHEN el operador ejecuta `sriyactl tenant current`
- THEN MUST mostrar `acme` + su tenantId con exit code 0

#### Scenario: no hay tenant activo

- GIVEN ningún tenant activo en el contexto
- WHEN se ejecuta `sriyactl tenant current`
- THEN MUST informar que no hay tenant activo con un `hint` para `tenant use`

## Out of scope (v2+)

Defer: `tenant update`, `tenant usage`, `apikey*` (rotación/listado), y operaciones de documento per-tenant con `X-API-Key`.
