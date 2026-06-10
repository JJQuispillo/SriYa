# cert Specification (sriyactl v1)

## Purpose

Comando `cert` de `sriyactl`: vigilancia de expiración de certificados SRI, cuya caducidad rompe la facturación en silencio. Usa auth **service-token** (`X-Service-Token`) contra el host del contexto activo. Pensado para correr en CI vía exit codes.

## Requirements

### Requirement: cert status

El comando `sriyactl cert status <tenant> --warn-days N` MUST leer el/los certificado(s) del tenant y reportar, para cada uno, fecha de expiración y estado (`valid` | `expiring` | `expired`). MUST marcar como `expiring` cuando la expiración cae dentro de N días y como `expired` cuando ya venció. MUST emitir un exit code señalizable para CI: 0 cuando todos válidos, distinto de 0 cuando alguno está `expiring` o `expired`.

#### Scenario: certificado vigente

- GIVEN un tenant con un certificado que expira en 90 días y `--warn-days 30`
- WHEN el operador ejecuta `sriyactl cert status acme --warn-days 30`
- THEN MUST reportar `status=valid` con la fecha de expiración y exit code 0

#### Scenario: certificado por expirar (señal CI)

- GIVEN un tenant con un certificado que expira en 10 días y `--warn-days 30`
- WHEN se ejecuta `sriyactl cert status acme --warn-days 30`
- THEN MUST reportar `status=expiring` con los días restantes
- AND el exit code MUST ser distinto de 0 para que CI lo detecte

#### Scenario: certificado expirado

- GIVEN un tenant con un certificado ya vencido
- WHEN se ejecuta `sriyactl cert status acme`
- THEN MUST reportar `status=expired` y exit code distinto de 0

#### Scenario: tenant sin certificado

- GIVEN un tenant que no tiene certificado cargado
- WHEN se ejecuta `sriyactl cert status acme`
- THEN MUST fallar con `code: cert_not_found` y un `hint` para subir el certificado

## Out of scope (v2+)

Defer: `cert upload` (carga/rotación de certificados) y notificaciones automáticas de expiración.
