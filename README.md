# Qora Billing API

Microservicio de facturacion electronica ecuatoriana para el ecosistema Qora POS. Genera, firma digitalmente (XAdES-BES), envia al SRI y almacena comprobantes electronicos conforme a la normativa vigente del Servicio de Rentas Internas del Ecuador.

## Self-Host vs Hosted

This repository is the **self-hostable** core of the Qora Billing API. You can
run it on your own infrastructure with Docker Compose and operate it for one or
more tenants. The defaults shipped here are tuned for a local/self-host setup:

- CORS defaults to `http://localhost:3000`.
- Secrets (DB password, service token, `Encryption.Key`) are **placeholders you
  MUST replace** before any non-development use.
- The reverse-proxy (Traefik) and TLS configuration are provided as an
  **optional commented block** in `docker-compose.yml` for hosted deployments.

A **hosted, multi-tenant** deployment additionally needs a public domain, a
reverse proxy with TLS (uncomment the Traefik labels), and externally managed
secrets. The same image and configuration surface support both modes — only the
environment values differ.

## Quick Start

### One-line install (recommended)

Pulls the pre-built image, generates strong secrets, and starts the stack — no
source checkout or .NET SDK required:

```bash
curl -fsSL https://raw.githubusercontent.com/JJQuispillo/SriYa/main/install.sh | bash
```

The installer creates a `qora-billing/` directory with a `.env` (random
secrets, `chmod 600`) and `docker-compose.yml`, then waits for `/health`.
Override the target dir or port with `INSTALL_DIR=... BILLING_PORT=...`.

> Review the generated `CORS_ORIGIN_0` and add TLS/reverse-proxy before
> exposing the service publicly.

### Manual (from source)

```bash
# 1. Clone and navigate
cd billing

# 2. Set environment variables
cp .env.example .env
# Edit .env with your values

# 3. Start with Docker Compose
docker compose up -d

# 4. Verify
curl http://localhost:8080/health
```

## API Overview

| Endpoint | Method | Description |
|---|---|---|
| `/health` | GET | Liveness check |
| `/health/ready` | GET | Readiness check (includes DB) |
| `/api/v1/documents/facturas` | POST | Create and submit a Factura (01) |
| `/api/v1/documents/liquidaciones-compra` | POST | Create and submit a Liquidación de Compra (03) |
| `/api/v1/documents/notas-credito` | POST | Create and submit a Nota de Crédito (04) |
| `/api/v1/documents/notas-debito` | POST | Create and submit a Nota de Débito (05) |
| `/api/v1/documents/guias-remision` | POST | Create and submit a Guía de Remisión (06) |
| `/api/v1/documents/retenciones` | POST | Create and submit a Comprobante de Retención (07) |
| `/api/documents/{id}` | GET | Get document by ID |
| `/api/documents` | GET | List documents (paginated, filtered by tenant) |
| `/api/documents/{id}/void` | POST | Void/annul a document |
| `/api/documents/{id}/events` | GET | Get document audit trail |
| `/api/documents/{id}/ride` | GET | Download RIDE (PDF representation) |
| `/api/tenants` | POST | Register a new tenant |
| `/api/tenants/{id}` | GET/PUT | Get or update tenant |
| `/api/tenants/{id}/certificates` | POST/GET | Upload or list signing certificates |
| `/api/tenants/{id}/api-keys` | POST/GET | Create or list API keys |
| `/api/tenants/{id}/api-keys/{keyId}/revoke` | POST | Revoke an API key |
| `/api/tenants/{id}/usage` | GET | Usage statistics |

Swagger UI is available at `/swagger` in Development mode.

## Configuration

| Variable | Description | Default |
|---|---|---|
| `ConnectionStrings__BillingDb` | PostgreSQL connection string | Required |
| `Sri__BaseUrlPruebas` | SRI test environment SOAP URL | SRI default |
| `Sri__BaseUrlProduccion` | SRI production SOAP URL | SRI default |
| `Sri__TimeoutSeconds` | SRI request timeout | `30` |
| `Sri__MaxRetries` | SRI retry attempts | `3` |
| `ServiceAuth__ServiceToken` | Token for internal service-to-service auth | Required |
| `Cors__AllowedOrigins__0` | Allowed CORS origin | `http://localhost:3000` |
| `ASPNETCORE_ENVIRONMENT` | Runtime environment | `Production` |

## Development Setup

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [PostgreSQL 16+](https://www.postgresql.org/) (or use Docker)
- [Docker](https://www.docker.com/) (optional, for containerized development)

### Local Development

```bash
# Restore dependencies
dotnet restore

# Start only the database
docker compose -f docker-compose.yml -f docker-compose.override.yml up billing-db -d

# Apply migrations
dotnet ef database update --project src/Qora.Billing.Infrastructure --startup-project src/Qora.Billing.Api

# Run the API
dotnet run --project src/Qora.Billing.Api

# The API starts on http://localhost:5100 (override) or http://localhost:8080
```

### Docker Development

```bash
# Build and run everything
docker compose -f docker-compose.yml -f docker-compose.override.yml up --build

# API: http://localhost:5100
# DB:  localhost:5433
```

## SRI Signing Certificate (.p12) Setup

To issue electronic documents you need an SRI-issued signing certificate
(a PKCS#12 `.p12`/`.pfx` file) per tenant.

1. **Set the encryption key.** Certificate private keys are encrypted at rest
   using `Encryption.Key` (in `appsettings.json` or the `Encryption__Key`
   environment variable). Replace the shipped `CHANGE_ME...` placeholder with a
   unique 32+ character secret before uploading any certificate, e.g.:

   ```bash
   openssl rand -base64 32
   ```

   Never commit a real key.

2. **Upload the certificate per tenant** via the API (the certificate and its
   password are stored encrypted in the database):

   ```bash
   curl -X POST http://localhost:8080/api/tenants/{tenantId}/certificates \
     -F "file=@/path/to/your-certificate.p12" \
     -F "password=YOUR_P12_PASSWORD"
   ```

   List uploaded certificates with `GET /api/tenants/{tenantId}/certificates`.

3. **(Optional) Host file mount.** If you prefer to provide the `.p12` from the
   host filesystem instead of uploading via the API, uncomment the `volumes`
   block under `billing-api` in `docker-compose.yml` and mount your `./certs`
   directory into the container.

## Architecture

The project follows **Clean Architecture** with four layers:

```
billing/
├── src/
│   ├── Qora.Billing.Domain/           # Entities, value objects, interfaces, domain events
│   ├── Qora.Billing.Application/      # Commands, queries, DTOs, handlers (MediatR)
│   ├── Qora.Billing.Infrastructure/   # EF Core, SRI SOAP client, XML/PDF generation, signing
│   └── Qora.Billing.Api/              # Minimal API endpoints, middleware, authentication
├── tests/
│   ├── Qora.Billing.UnitTests/        # Unit tests (domain, application layer)
│   └── Qora.Billing.IntegrationTests/ # Integration tests
├── Dockerfile
├── docker-compose.yml
└── Qora.Billing.sln
```

**Key patterns:**
- CQRS via MediatR (commands and queries separated)
- Repository + Unit of Work
- Strategy pattern for document types (Factura, NotaCredito, etc.)
- Polly resilience (retry + circuit breaker) for SRI SOAP calls
- Multi-tenant via API key authentication
- Domain events for audit trail

## Testing

```bash
# Run all tests
dotnet test

# Run unit tests only
dotnet test tests/Qora.Billing.UnitTests

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Check formatting
dotnet format --verify-no-changes
```

## License

This project is open source under the [MIT License](./LICENSE).

Copyright (c) 2026 qoraSystems
