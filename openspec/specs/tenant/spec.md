# tenant Specification (sriyactl v1)

## Purpose

Comandos `tenant` de `sriyactl`: onboarding atómico y modelo de tenant activo estilo kubectl. Todos usan auth **service-token** (`X-Service-Token`) contra el host del **contexto activo**. Los tenants viven dentro de un contexto (host URL + service-token) en `~/.config/sriyactl/config.toml` (solo no-secrets); el `apiKey` per-tenant vive en el OS keychain.

## Requirements

### Requirement: tenant create

El comando `sriyactl tenant create` MUST hacer onboarding atómico vía `POST /api/v1/bootstrap`
con header `X-Service-Token`. Ante RUC duplicado el backend responde **HTTP 400 BadRequest**
con un `ProblemDetails` cuyo `detail` indica que ya existe un tenant con ese RUC (NO 409).
El CLI MUST detectar el 400 + la señal de duplicado en el `ProblemDetails` y mapearlo a
`code: tenant_duplicate` con exit code 5; MUST NOT volcar el `ProblemDetails` crudo como error
genérico de exit 1, y MUST NOT registrar alias ni escribir el `apiKey` en el keychain.
La rama de mapeo a 409 MUST eliminarse (código muerto).

(Previamente: el CLI mapeaba RUC duplicado a HTTP 409; como el backend devuelve 400, caía en
la rama genérica → exit 1 volcando el `ProblemDetails` crudo.)

#### Scenario: onboarding exitoso

- GIVEN un contexto activo con service-token válido y datos de tenant nuevos
- WHEN el operador ejecuta `sriyactl tenant create --ruc <ruc> --alias acme`
- THEN MUST llamar `POST /api/v1/bootstrap` con `X-Service-Token`
- AND MUST guardar el `apiKey` en el keychain bajo `acme` y registrar el alias en config
- AND MUST devolver `tenantId` sin exponer el `apiKey` en stdout, con exit code 0

#### Scenario: RUC duplicado → 400 → tenant_duplicate / exit 5

- GIVEN un tenant ya existe para ese RUC y el backend responde `400 BadRequest` con `ProblemDetails` de RUC duplicado
- WHEN se ejecuta `sriyactl tenant create --ruc <ruc> --alias acme`
- THEN el CLI MUST mapear el 400 a `code: tenant_duplicate` y exit code 5 (NO exit 1 genérico)
- AND MUST NOT registrar alias ni escribir en el keychain

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
