# ai-contract Specification (sriyactl v1)

## Purpose

Contrato transversal **AI-friendly** que aplica a TODOS los comandos de `sriyactl`. Garantiza salida estructurada, modo no-interactivo automático, exit codes deterministas y bloqueo de mutaciones en modo read-only. Implementado vía separación estricta handler ↔ render (los handlers devuelven datos tipados; la capa de presentación renderiza el formato).

## Requirements

### Requirement: salida estructurada

Todo comando MUST aceptar `--output json|yaml|table`. La salida `json`/`yaml` MUST envolver el resultado en un envelope que incluya `schemaVersion`. `table` es para humanos; `json`/`yaml` son estables y parseables por máquina.

#### Scenario: salida json con schemaVersion

- GIVEN cualquier comando de lectura
- WHEN se ejecuta con `--output json`
- THEN la salida MUST ser JSON válido con un campo `schemaVersion` en el envelope

#### Scenario: salida table por defecto en TTY

- GIVEN stdout es un TTY y no se pasa `--output`
- WHEN se ejecuta un comando de lectura
- THEN la salida MUST renderizarse como `table` legible para humanos

### Requirement: modo no-TTY automático

Cuando stdout/stdin NO es un TTY, el output por defecto MUST ser `json` y todos los prompts interactivos MUST suprimirse (asumiendo no-interactivo). Un `--output` explícito MUST tener prioridad sobre este default.

#### Scenario: pipe fuerza json

- GIVEN stdout no es un TTY (p. ej. salida redirigida a un pipe) y no se pasa `--output`
- WHEN se ejecuta un comando de lectura
- THEN la salida por defecto MUST ser `json` y MUST NOT emitirse ningún prompt

#### Scenario: override explícito en no-TTY

- GIVEN stdout no es un TTY y se pasa `--output table`
- WHEN se ejecuta el comando
- THEN MUST respetar `table` pese al default no-TTY

### Requirement: exit codes deterministas

Todo comando MUST devolver exit codes deterministas: 0 en éxito y códigos no-cero estables por clase de error. El mismo error en las mismas condiciones MUST producir siempre el mismo exit code.

#### Scenario: éxito devuelve 0

- GIVEN un comando que completa correctamente
- WHEN termina
- THEN el exit code MUST ser 0

#### Scenario: error de clase estable

- GIVEN una condición de error reproducible
- WHEN el comando falla por esa condición dos veces
- THEN ambas ejecuciones MUST devolver el mismo exit code no-cero

### Requirement: errores como JSON estructurado

Cuando `output=json` (o no-TTY), los errores MUST emitirse como un objeto JSON `{code, message, hint, retryable}` en lugar de texto libre. `retryable` MUST indicar si reintentar puede tener éxito.

#### Scenario: error en modo json

- GIVEN `--output json`
- WHEN el comando falla
- THEN MUST emitir un objeto `{code, message, hint, retryable}` y exit code no-cero

### Requirement: dry-run y modo no-interactivo

`--dry-run` MUST devolver las acciones planificadas como datos SIN ejecutar efectos secundarios.
Los comandos destructivos MUST ser interactivos por defecto en TTY (pedir confirmación) y MUST
volverse no-interactivos con `--yes`/`--no-input` (proceder sin prompt). Cuando un comando
destructivo corre en no-TTY SIN `--yes`/`--no-input`, MUST rehusar con error claro
(`code: confirmation_required`) y exit code no-cero, SIN ejecutar el efecto — NO MUST asumir
confirmación implícita en non-TTY para ops destructivas.

(Previamente el contrato no exigía rehusar en non-TTY sin `--yes`; permitía asumir no-interactivo
y arriesgaba ejecutar ops destructivas sin confirmación explícita.)

#### Scenario: dry-run sin efectos

- GIVEN un comando mutador con `--dry-run`
- WHEN se ejecuta
- THEN MUST reportar las acciones planificadas como datos y NO producir efectos secundarios, con exit code 0

#### Scenario: confirmación no-interactiva con --yes

- GIVEN un comando destructivo en TTY con `--yes`
- WHEN se ejecuta
- THEN MUST proceder sin prompt de confirmación

#### Scenario: destructivo en non-TTY sin --yes rehúsa

- GIVEN un comando destructivo, stdout no-TTY y SIN `--yes`/`--no-input`
- WHEN se ejecuta
- THEN MUST rehusar con `code: confirmation_required` y exit code no-cero, SIN efecto

### Requirement: read-only bloquea mutaciones

Cuando `SRIYACTL_READONLY=1` está activo (o el contexto activo es read-only), todo comando mutador MUST ser rechazado antes de ejecutar efectos, con un error claro. Los comandos de solo lectura MUST seguir funcionando.

#### Scenario: mutación bloqueada en read-only

- GIVEN `SRIYACTL_READONLY=1`
- WHEN se ejecuta un comando mutador (p. ej. `tenant create` o `infra upgrade`)
- THEN MUST fallar con `code: readonly_blocked` antes de cualquier efecto, con exit code no-cero

#### Scenario: lectura permitida en read-only

- GIVEN `SRIYACTL_READONLY=1`
- WHEN se ejecuta un comando de solo lectura (p. ej. `infra status`)
- THEN MUST ejecutarse normalmente con exit code 0

### Requirement: payload con centinela no se descarta

Cuando un handler devuelve datos NO vacíos junto con un error-centinela no-fatal
(p. ej. cert `expiring`/`expired`; infra status `degraded`/`ready=down`), el runner del CLI
MUST emitir primero el payload de datos a stdout (table/json según `--output`), luego escribir
el error a stderr, y recién entonces retornar el exit code no-cero. El payload MUST NOT
descartarse cuando `err != nil`.

(Antes el runner descartaba `out` siempre que `err != nil`, perdiendo el payload de sentinels.)

#### Scenario: cert expiring emite payload y señaliza

- GIVEN `cert status` con un cert `expiring` (dato + centinela)
- WHEN se ejecuta con `--output json`
- THEN MUST emitir el JSON del cert a stdout Y el error a stderr, retornando exit code distinto de 0

#### Scenario: infra status degradado emite payload y señaliza

- GIVEN `infra status` con `ready=down` (dato + centinela)
- WHEN se ejecuta
- THEN MUST emitir el estado del stack a stdout Y la degradación a stderr, con exit code distinto de 0

### Requirement: exit codes distintos y estables por clase

Cada clase de error señalizable MUST tener un exit code no-cero estable y DISTINTO, sin colapsar
clases no relacionadas en el código retryable de red. En particular: `cert_expiring` y
`cert_expired` MUST tener códigos distintos entre sí y separados de la clase network/retryable;
`upgrade_health_timeout` y `doctor_check_failed` MUST tener códigos distintos entre sí y
separados de la clase retryable. `tenant_duplicate` MUST mapear a exit 5. Esto permite a CI
ramificar por código.

#### Scenario: cert_expiring y cert_expired difieren

- GIVEN dos ejecuciones de `cert status`, una con cert `expiring` y otra con `expired`
- WHEN ambas terminan
- THEN MUST devolver exit codes no-cero DISTINTOS entre sí y distintos del código retryable de red

#### Scenario: timeouts de infra no colapsan en retryable

- GIVEN `upgrade_health_timeout` y `doctor_check_failed`
- WHEN cada condición ocurre
- THEN cada una MUST devolver un exit code estable distinto entre sí y distinto de la clase retryable

### Requirement: tests afirman el contrato real del backend

Los tests unitarios CORREGIDOS MUST afirmar el contrato REAL del backend .NET, reemplazando las
aserciones fabricadas. Concretamente: RUC duplicado MUST testearse contra **HTTP 400** (no el
409 inventado); el DTO de certificado MUST testearse con los campos JSON reales
`{id, nombrePropietario, fechaExpiracion, activo, fechaCreacion}` (no `expiresAt`/`estado`);
lista de certs vacía MUST testearse como `200 []` → `cert_not_found`; readiness MUST testearse
contra `GET /health/ready` distinto de `GET /health`. Los tests previos que afirman 409 o el DTO
equivocado MUST eliminarse, no conservarse.

#### Scenario: el test de duplicado usa 400

- GIVEN un stub de backend que responde `400` con `ProblemDetails` de RUC duplicado
- WHEN corre el test de `tenant create` duplicado
- THEN MUST aseverar `code: tenant_duplicate`/exit 5 y MUST NOT existir ya un test que asevere 409

#### Scenario: el test de cert usa los campos reales

- GIVEN un stub que devuelve `[{"fechaExpiracion":"...","activo":true,...}]`
- WHEN corre el test de `cert status`
- THEN MUST decodificar `fechaExpiracion` real y aseverar el estado correcto (un cert vigente → `valid`)

## Out of scope (v2+)

Defer: `sriyactl spec --json` (contrato de comandos), `sriyactl mcp` (MCP server), `--field` selection, JSONL en listas (v3).
