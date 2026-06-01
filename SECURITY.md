# Política de seguridad

## Versiones soportadas

Este proyecto está en desarrollo activo. Las correcciones de seguridad se
aplican sobre la rama principal y la última versión publicada.

## Reportar una vulnerabilidad

**Por favor, no reportes vulnerabilidades de seguridad mediante issues públicos.**

Usa el canal privado de **GitHub Security Advisories** del repositorio:

1. Ve a la pestaña **Security** de `JJQuispillo/SriYa`.
2. Selecciona **Report a vulnerability** (Private vulnerability reporting).
3. Describe el problema, el impacto y, de ser posible, pasos para reproducirlo.

Procuraremos responder en un plazo razonable, confirmar la recepción y
mantenerte informado del avance hasta su resolución.

## Alcance

Dado que esta es una API de facturación electrónica que maneja certificados de
firma (.p12), claves de cifrado y datos tributarios, son especialmente
relevantes los reportes sobre:

- Exposición o filtrado de certificados/credenciales.
- Fallos en el cifrado de datos sensibles en reposo.
- Saltos de aislamiento entre tenants (multi-tenancy).
- Inyección, deserialización insegura o RCE.
- Errores de autenticación/autorización en los endpoints.

## Buenas prácticas para quien auto-hospeda

- Reemplaza **siempre** `Encryption:Key` y `ServiceAuth:ServiceToken` por
  valores propios y secretos antes de cualquier uso fuera de desarrollo.
- Nunca commitees tu archivo `.env` ni certificados `.p12`.
- Mantén la base de datos y la API detrás de TLS.
