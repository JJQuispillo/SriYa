# Delta for tenant (sriyactl v1 fixes)

Corrige el mapeo fabricado de RUC duplicado. Backend verificado:
RUC duplicado lanza `BillingDomainException`
(`src/Qora.Billing.Infrastructure/Persistence/TenantBootstrapService.cs:67`,
también `src/Qora.Billing.Application/Commands/Handlers/CreateTenantCommandHandler.cs:25`),
que `GlobalExceptionHandler.cs:106-112` mapea a **400 BadRequest**. El único 409 del backend
es `SecuencialExhaustedException` (`GlobalExceptionHandler.cs:98-104`), no relacionado.

## MODIFIED Requirements

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
