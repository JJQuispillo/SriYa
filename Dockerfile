# ============================================================================
# Qora Billing API — Multi-stage Dockerfile
# ============================================================================

# Stage 1: Restore dependencies (cacheable layer)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore
WORKDIR /src

COPY Directory.Build.props .
COPY Qora.Billing.sln .
COPY src/Qora.Billing.Domain/Qora.Billing.Domain.csproj src/Qora.Billing.Domain/
COPY src/Qora.Billing.Application/Qora.Billing.Application.csproj src/Qora.Billing.Application/
COPY src/Qora.Billing.Infrastructure/Qora.Billing.Infrastructure.csproj src/Qora.Billing.Infrastructure/
COPY src/Qora.Billing.Api/Qora.Billing.Api.csproj src/Qora.Billing.Api/

RUN dotnet restore src/Qora.Billing.Api/Qora.Billing.Api.csproj

# Stage 2: Build
FROM restore AS build
COPY src/ src/
RUN dotnet build src/Qora.Billing.Api/Qora.Billing.Api.csproj \
    -c Release \
    --no-restore

# Stage 3: Publish
FROM build AS publish
RUN dotnet publish src/Qora.Billing.Api/Qora.Billing.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-build

# Stage 4: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime

LABEL maintainer="Qora Team <dev@qora.app>"
LABEL org.opencontainers.image.title="Qora Billing API"
LABEL org.opencontainers.image.description="Ecuadorian electronic invoicing microservice"
LABEL org.opencontainers.image.version="1.0.0"
LABEL org.opencontainers.image.source="https://github.com/qora/billing"

# Install curl for health checks, then clean up
RUN apk add --no-cache curl

WORKDIR /app

# Create non-root user
RUN addgroup --system --gid 1001 appgroup && \
    adduser --system --uid 1001 -G appgroup appuser

COPY --from=publish /app/publish .

# Set ownership
RUN chown -R appuser:appgroup /app

USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Qora.Billing.Api.dll"]
