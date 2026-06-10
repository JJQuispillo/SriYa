# SriYa API

Microservicio de facturación electrónica ecuatoriana para el ecosistema Qora POS. Genera, firma digitalmente (XAdES-BES), envía al SRI y almacena comprobantes electrónicos conforme a la normativa vigente del Servicio de Rentas Internas del Ecuador.

## Self-Host vs. Hosted

Este repositorio es el núcleo **autoalojable** (self-host) de SriYa. Puedes ejecutarlo en tu propia infraestructura con Docker Compose y operarlo para uno o varios tenants. Los valores por defecto vienen ajustados para un entorno local/self-host:

- CORS apunta por defecto a `http://localhost:3000`.
- Los secretos (contraseña de la base de datos, token de servicio y `Encryption.Key`) son **marcadores que DEBES reemplazar** antes de cualquier uso fuera de desarrollo.
- El proxy inverso (Traefik) y la configuración de TLS se entregan como un **bloque comentado opcional** en `docker-compose.yml` para despliegues hosted.

Un despliegue **hosted y multi-tenant** necesita además un dominio público, un proxy inverso con TLS (descomenta las labels de Traefik) y secretos gestionados externamente. La misma imagen y la misma configuración soportan ambos modos: solo cambian los valores del entorno.

## Inicio rápido

### Instalación en una línea (recomendado)

Descarga la imagen pre-construida, genera secretos robustos y levanta el stack — sin necesidad de clonar el código ni instalar el SDK de .NET:

```bash
curl -fsSL https://raw.githubusercontent.com/JJQuispillo/SriYa/main/install.sh | bash
```

El instalador crea un directorio `qora-billing/` con un `.env` (secretos aleatorios, `chmod 600`) y un `docker-compose.yml`, luego espera a que `/health` responda. Al terminar imprime el comando listo para copiar que da de alta tu primer emisor (tenant + certificado `.p12` + API key, de forma atómica).

Por defecto instala una versión fijada (`VERSION=1.0.0`): tanto la imagen como el `docker-compose.yml` quedan ancladas al mismo release. Puedes ajustar valores no secretos con variables de entorno (funcionan también a través de `curl | bash`):

```bash
VERSION=1.0.0 \
BILLING_PORT=8080 \
CORS_ORIGIN_0=https://app.tudominio.com \
BILLING_DB_USER=billing_user \
INSTALL_DIR=qora-billing \
  bash install.sh
```

Si en cambio ejecutas el script **directamente en una terminal** (`bash install.sh`), te preguntará interactivamente esos mismos valores. Los secretos (contraseñas, token de servicio, clave de cifrado) **nunca se preguntan**: siempre se generan aleatorios.

> Revisa el `CORS_ORIGIN_0` generado y añade TLS/proxy inverso antes de exponer el servicio públicamente.

> ¿Prefieres inspeccionar el script antes de ejecutarlo? Descárgalo, léelo y luego córrelo:
> ```bash
> curl -fsSL https://raw.githubusercontent.com/JJQuispillo/SriYa/main/install.sh -o install.sh
> less install.sh && bash install.sh
> ```

### Manual (desde el código fuente)

```bash
# 1. Clona y entra al proyecto
cd billing

# 2. Configura las variables de entorno
cp .env.example .env
# Edita .env con tus valores

# 3. Levanta con Docker Compose
docker compose up -d

# 4. Verifica
curl http://localhost:8080/health
```

## Operación (día 2)

Todos los comandos se ejecutan desde el directorio de instalación (`qora-billing/` por defecto), donde viven el `.env` y el `docker-compose.yml`.

### Actualizar a una versión nueva

```bash
# Edita BILLING_IMAGE_TAG en .env (p. ej. 1.1.0), luego:
docker compose pull
docker compose up -d
```

Las migraciones de base de datos se aplican solas al arrancar el contenedor. No hay paso manual de DBA.

### Respaldo y restauración

El estado vive en el volumen `billing-db-data` (PostgreSQL). Respáldalo con `pg_dump`:

```bash
# Backup (comprimido, con marca de tiempo)
docker compose exec -T billing-db \
  pg_dump -U billing_user -d qora_billing | gzip > backup-$(date +%F).sql.gz

# Restauración (sobre una base vacía/recién instalada)
gunzip -c backup-2026-06-05.sql.gz | \
  docker compose exec -T billing-db psql -U billing_user -d qora_billing
```

> Si cambiaste `BILLING_DB_USER`, ajusta el `-U` en ambos comandos.

### Logs y estado

```bash
docker compose ps                 # estado de los contenedores
docker compose logs -f billing-api  # logs de la API en vivo
```

### Desinstalar

```bash
docker compose down        # detiene y elimina los contenedores (CONSERVA los datos)
docker compose down -v     # ⚠️ elimina TAMBIÉN el volumen: se pierden todos los datos
```

## Resumen de la API

| Endpoint | Método | Descripción |
|---|---|---|
| `/health` | GET | Chequeo de vida (liveness) |
| `/health/ready` | GET | Chequeo de disponibilidad (readiness, incluye DB) |
| `/api/v1/documents/facturas` | POST | Crea y envía una Factura (01) |
| `/api/v1/documents/liquidaciones-compra` | POST | Crea y envía una Liquidación de Compra (03) |
| `/api/v1/documents/notas-credito` | POST | Crea y envía una Nota de Crédito (04) |
| `/api/v1/documents/notas-debito` | POST | Crea y envía una Nota de Débito (05) |
| `/api/v1/documents/guias-remision` | POST | Crea y envía una Guía de Remisión (06) |
| `/api/v1/documents/retenciones` | POST | Crea y envía un Comprobante de Retención (07) |
| `/api/documents/{id}` | GET | Obtiene un documento por ID |
| `/api/documents` | GET | Lista documentos (paginado, filtrado por tenant) |
| `/api/documents/{id}/void` | POST | Anula un documento |
| `/api/documents/{id}/events` | GET | Obtiene el historial de auditoría del documento |
| `/api/documents/{id}/ride` | GET | Descarga el RIDE (representación en PDF) |
| `/api/tenants` | POST | Registra un nuevo tenant |
| `/api/tenants/{id}` | GET/PUT | Obtiene o actualiza un tenant |
| `/api/tenants/{id}/certificates` | POST/GET | Sube o lista certificados de firma |
| `/api/tenants/{id}/api-keys` | POST/GET | Crea o lista API keys |
| `/api/tenants/{id}/api-keys/{keyId}/revoke` | POST | Revoca una API key |
| `/api/tenants/{id}/usage` | GET | Estadísticas de uso |

La interfaz de Swagger está disponible en `/swagger` en modo Development.

## Configuración

| Variable | Descripción | Valor por defecto |
|---|---|---|
| `ConnectionStrings__BillingDb` | Cadena de conexión a PostgreSQL | Requerido |
| `Encryption__Key` | Clave para cifrar en reposo las llaves de los certificados (32+ caracteres) | Requerido |
| `Sri__BaseUrlPruebas` | URL SOAP del entorno de pruebas del SRI | Default del SRI |
| `Sri__BaseUrlProduccion` | URL SOAP del entorno de producción del SRI | Default del SRI |
| `Sri__TimeoutSeconds` | Timeout de las peticiones al SRI | `30` |
| `Sri__MaxRetries` | Reintentos hacia el SRI | `3` |
| `ServiceAuth__ServiceToken` | Token para la autenticación servicio-a-servicio | Requerido |
| `Cors__AllowedOrigins__0` | Origen CORS permitido | `http://localhost:3000` |
| `ASPNETCORE_ENVIRONMENT` | Entorno de ejecución | `Production` |

## Entorno de desarrollo

### Requisitos previos

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [PostgreSQL 16+](https://www.postgresql.org/) (o usa Docker)
- [Docker](https://www.docker.com/) (opcional, para desarrollo en contenedores)

### Desarrollo local

```bash
# Restaura dependencias
dotnet restore

# Levanta solo la base de datos
docker compose -f docker-compose.yml -f docker-compose.override.yml up billing-db -d

# Aplica las migraciones
dotnet ef database update --project src/Qora.Billing.Infrastructure --startup-project src/Qora.Billing.Api

# Ejecuta la API
dotnet run --project src/Qora.Billing.Api

# La API arranca en http://localhost:5100 (override) o http://localhost:8080
```

### Desarrollo con Docker

```bash
# Compila y levanta todo
docker compose -f docker-compose.yml -f docker-compose.override.yml up --build

# API: http://localhost:5100
# DB:  localhost:5433
```

## Configuración del certificado de firma del SRI (.p12)

Para emitir documentos electrónicos necesitas un certificado de firma emitido por el SRI (un archivo PKCS#12 `.p12`/`.pfx`) por cada tenant.

1. **Define la clave de cifrado.** Las llaves privadas de los certificados se cifran en reposo con `Encryption.Key` (en `appsettings.json` o en la variable de entorno `Encryption__Key`). Reemplaza el marcador `CHANGE_ME...` por un secreto único de 32+ caracteres antes de subir cualquier certificado, por ejemplo:

   ```bash
   openssl rand -base64 32
   ```

   Nunca subas una clave real al repositorio.

2. **Sube el certificado por tenant** vía la API (el certificado y su contraseña se almacenan cifrados en la base de datos):

   ```bash
   curl -X POST http://localhost:8080/api/tenants/{tenantId}/certificates \
     -F "file=@/ruta/a/tu-certificado.p12" \
     -F "password=TU_PASSWORD_DEL_P12"
   ```

   Lista los certificados subidos con `GET /api/tenants/{tenantId}/certificates`.

3. **(Opcional) Montaje desde el host.** Si prefieres entregar el `.p12` desde el sistema de archivos del host en lugar de subirlo por la API, descomenta el bloque `volumes` bajo `billing-api` en `docker-compose.yml` y monta tu directorio `./certs` dentro del contenedor.

## CLI (sriyactl)

The day-2 ops CLI for the SriYa ecosystem lives at [`cli/`](./cli/). It manages
tenants, certificates, upgrades, backups, and infrastructure for the self-hosted
stack. Full documentation is in [`cli/README.md`](./cli/README.md).

### Quick start (from source)

```bash
cd cli
go build -o ../bin/sriyactl ./cmd/sriyactl
```

Or from the repo root using the Makefile:

```bash
make go-build   # build the CLI binary
make go-test    # run CLI tests
make go-check   # vet + test + build
```

### One-line binary install

```bash
curl -fsSL https://raw.githubusercontent.com/JJQuispillo/billing/main/cli/install.sh | bash
```

This downloads the pre-built binary from GitHub Releases (published via goreleaser).

## Arquitectura

El proyecto sigue **Clean Architecture** con cuatro capas:

```
billing/
├── src/
│   ├── Qora.Billing.Domain/           # Entidades, value objects, interfaces, eventos de dominio
│   ├── Qora.Billing.Application/      # Comandos, queries, DTOs, handlers (MediatR)
│   ├── Qora.Billing.Infrastructure/   # EF Core, cliente SOAP del SRI, generación XML/PDF, firma
│   └── Qora.Billing.Api/              # Endpoints Minimal API, middleware, autenticación
├── tests/
│   ├── Qora.Billing.UnitTests/        # Pruebas unitarias (dominio, capa de aplicación)
│   └── Qora.Billing.IntegrationTests/ # Pruebas de integración
├── Dockerfile
├── docker-compose.yml
└── Qora.Billing.sln
```

**Patrones clave:**
- CQRS con MediatR (comandos y queries separados)
- Repository + Unit of Work
- Strategy para los tipos de documento (Factura, NotaCredito, etc.)
- Resiliencia con Polly (retry + circuit breaker) para las llamadas SOAP al SRI
- Multi-tenant mediante autenticación por API key
- Eventos de dominio para el historial de auditoría

## Pruebas

```bash
# Ejecuta todas las pruebas
dotnet test

# Solo pruebas unitarias
dotnet test tests/Qora.Billing.UnitTests

# Con cobertura
dotnet test --collect:"XPlat Code Coverage"

# Verifica el formato
dotnet format --verify-no-changes
```

## Licencia

Este proyecto es de código abierto bajo la [Licencia MIT](./LICENSE).

Copyright (c) 2026 qoraSystems
