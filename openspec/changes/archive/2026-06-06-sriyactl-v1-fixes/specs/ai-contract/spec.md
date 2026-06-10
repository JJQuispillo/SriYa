# Delta for ai-contract (sriyactl v1 fixes)

Endurece el contrato transversal: payload+centinela no se descarta, ops destructivas
no-interactivas rehúsan, exit codes distintos y estables, y los tests afirman el contrato REAL.
Backend verificado para los códigos derivados:
`src/Qora.Billing.Api/Middleware/GlobalExceptionHandler.cs:98-112` (409 sólo `SecuencialExhaustedException`; duplicado=400),
`src/Qora.Billing.Application/DTOs/CertificateDtos.cs:3-8` (campos reales del cert),
`src/Qora.Billing.Api/Endpoints/HealthEndpoints.cs:24-49` (`/health/ready` distinto de `/health`).

## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: dry-run y modo no-interactivo

`--dry-run` MUST devolver las acciones planificadas como datos SIN ejecutar efectos secundarios.
Los comandos destructivos MUST ser interactivos por defecto en TTY (pedir confirmación) y MUST
volverse no-interactivos con `--yes`/`--no-input` (proceder sin prompt). Cuando un comando
destructivo corre en no-TTY SIN `--yes`/`--no-input`, MUST rehusar con error claro
(`code: confirmation_required`) y exit code no-cero, SIN ejecutar el efecto — NO MUST asumir
confirmación implícita en non-TTY para ops destructivas.

(Previamente el contrato no exigía rehusar en non-TTY sin `--yes`; permitía asumir no-interactivo
y arriesgaba ejecutar ops destructivas sin confirmación explícita.)

#### Scenario: confirmación no-interactiva con --yes

- GIVEN un comando destructivo en TTY con `--yes`
- WHEN se ejecuta
- THEN MUST proceder sin prompt de confirmación

#### Scenario: destructivo en non-TTY sin --yes rehúsa

- GIVEN un comando destructivo, stdout no-TTY y SIN `--yes`/`--no-input`
- WHEN se ejecuta
- THEN MUST rehusar con `code: confirmation_required` y exit code no-cero, SIN efecto

## ADDED Requirements

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
