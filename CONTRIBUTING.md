# Guía de contribución

¡Gracias por tu interés en contribuir! Este proyecto es una API de facturación
electrónica para el SRI (Ecuador), open-source y self-hostable, construida en
.NET 9 con Clean Architecture.

## Requisitos

- [.NET SDK 9.0](https://dotnet.microsoft.com/download)
- Docker y Docker Compose (opcional, para levantar PostgreSQL local)
- PostgreSQL 15+ (si no usas Docker)

## Puesta en marcha

```bash
# 1. Restaurar herramientas locales (incluye dotnet-ef)
dotnet tool restore

# 2. Restaurar dependencias y compilar
dotnet build Qora.Billing.sln

# 3. Configurar entorno
cp .env.example .env
# edita .env con tus credenciales locales

# 4. Levantar la base de datos (opcional, vía Docker)
docker compose up -d

# 5. Aplicar migraciones
dotnet ef database update --project src/Qora.Billing.Infrastructure --startup-project src/Qora.Billing.Api

# 6. Ejecutar la API
dotnet run --project src/Qora.Billing.Api
```

## Ejecutar las pruebas

```bash
dotnet test Qora.Billing.sln
```

## Estilo de código

- Sigue las convenciones de `.editorconfig` del repositorio.
- Verifica el formato antes de commitear: `dotnet format --verify-no-changes`.
- Respeta la separación de capas de Clean Architecture: `Domain` no depende de
  ninguna otra capa; `Application` orquesta; `Infrastructure` implementa puertos;
  `Api` solo expone endpoints.

## Migraciones de base de datos

Si tu cambio modifica el modelo de EF Core, genera una migración:

```bash
dotnet ef migrations add NombreDescriptivo \
  --project src/Qora.Billing.Infrastructure \
  --startup-project src/Qora.Billing.Api \
  --output-dir Migrations
```

## Commits

Usamos [Conventional Commits](https://www.conventionalcommits.org/) en español:

- `feat:` nueva funcionalidad
- `fix:` corrección de error
- `refactor:` cambio interno sin alterar comportamiento
- `chore:` tareas de mantenimiento / configuración
- `docs:` documentación
- `test:` pruebas

Ejemplo: `feat: soporte para nota de débito en el generador XML`

## Pull Requests

1. Haz fork del repositorio y crea una rama descriptiva.
2. Asegúrate de que `dotnet build` y `dotnet test` pasen.
3. Describe el cambio y enlaza el issue relacionado (si aplica).
4. Un mantenedor revisará tu PR. Pueden solicitarse ajustes antes del merge.

## Reportar problemas de seguridad

No abras un issue público para vulnerabilidades. Consulta
[SECURITY.md](./SECURITY.md).
