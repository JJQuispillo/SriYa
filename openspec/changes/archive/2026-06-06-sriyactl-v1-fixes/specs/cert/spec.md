# Delta for cert (sriyactl v1 fixes)

Corrige el contrato fabricado del DTO de certificado. Backend verificado:
`src/Qora.Billing.Application/DTOs/CertificateDtos.cs:3-8` (record `CertificateResponse`,
JSON camelCase por defecto de System.Text.Json) y
`src/Qora.Billing.Api/Endpoints/CertificateEndpoints.cs:72-80`
(`GET /api/v1/certificates/` → `Ok<List<CertificateResponse>>`, `200 []` cuando vacío).

## MODIFIED Requirements

### Requirement: cert status

El comando `sriyactl cert status <tenant> --warn-days N` MUST decodificar la respuesta
real del backend: `GET /api/v1/certificates/` devuelve `200` con una lista JSON de objetos
`{id, nombrePropietario, fechaExpiracion, activo, fechaCreacion}`. El estado MUST derivarse
del campo real `fechaExpiracion` (no de un `expiresAt` inexistente que decodifica a zero-time):
`valid` si `fechaExpiracion` es futura y fuera de N días; `expiring` si cae dentro de N días;
`expired` si ya pasó. Un certificado vigente MUST reportarse `valid`, NUNCA `expired`.
El exit code MUST ser 0 si todos válidos, y distinto de 0 si alguno está `expiring` o `expired`.

(Previamente: el DTO Go esperaba `subject, issuer, expiresAt, estado`; al no existir
`expiresAt` en el backend, `ExpiresAt` decodificaba a zero-time y TODO cert se marcaba `expired`.)

#### Scenario: certificado vigente reporta valid (no expired)

- GIVEN el backend devuelve `200 [{"id":"...","nombrePropietario":"ACME","fechaExpiracion":"<hoy+90d>","activo":true,"fechaCreacion":"..."}]` y `--warn-days 30`
- WHEN el operador ejecuta `sriyactl cert status acme --warn-days 30`
- THEN MUST decodificar `fechaExpiracion` correctamente y reportar `status=valid` con esa fecha
- AND el exit code MUST ser 0

#### Scenario: certificado por expirar dentro de warn-days

- GIVEN el backend devuelve un cert con `fechaExpiracion` a 10 días y `--warn-days 30`
- WHEN se ejecuta `sriyactl cert status acme --warn-days 30`
- THEN MUST reportar `status=expiring` con los días restantes derivados de `fechaExpiracion`
- AND MUST emitir el payload del cert (table/json) Y señalizar vía stderr + exit code distinto de 0

#### Scenario: certificado expirado

- GIVEN el backend devuelve un cert con `fechaExpiracion` en el pasado
- WHEN se ejecuta `sriyactl cert status acme`
- THEN MUST reportar `status=expired` derivado de `fechaExpiracion`
- AND MUST emitir el payload del cert Y señalizar vía stderr + exit code distinto de 0

#### Scenario: tenant sin certificado (lista vacía 200 [])

- GIVEN el backend devuelve `200 []` (tenant sin certificado cargado)
- WHEN se ejecuta `sriyactl cert status acme`
- THEN MUST detectar la lista vacía y fallar con `code: cert_not_found` y un `hint` para subir el certificado
- AND el exit code MUST ser distinto de 0 (NO exit 0 con lista vacía)
