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

`--dry-run` MUST devolver las acciones planificadas como datos SIN ejecutar efectos secundarios. Los comandos destructivos MUST ser interactivos por defecto en TTY y MUST volverse no-interactivos con `--yes`/`--no-input`.

#### Scenario: dry-run sin efectos

- GIVEN un comando mutador con `--dry-run`
- WHEN se ejecuta
- THEN MUST reportar las acciones planificadas como datos y NO producir efectos secundarios, con exit code 0

#### Scenario: confirmación no-interactiva

- GIVEN un comando destructivo en TTY con `--yes`
- WHEN se ejecuta
- THEN MUST proceder sin prompt de confirmación

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

## Out of scope (v2+)

Defer: `sriyactl spec --json` (contrato de comandos), `sriyactl mcp` (MCP server), `--field` selection, JSONL en listas (v3).
