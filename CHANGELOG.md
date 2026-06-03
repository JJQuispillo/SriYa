# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-06-02

### Added
- One-line self-host installer (`install.sh`) that generates secrets and starts
  the stack via Docker Compose.
- `docker-compose.prod.yml` that runs the pre-built GHCR image (no source
  checkout or .NET SDK required).
- Release workflow publishing multi-arch (amd64/arm64) images to
  `ghcr.io/jjquispillo/sriya` on `v*.*.*` tags.
- `ENCRYPTION_KEY` wired through `.env.example` and Docker Compose.
- Electronic document endpoints for the six SRI types (Factura, Liquidación de
  Compra, Nota de Crédito, Nota de Débito, Guía de Remisión, Retención),
  XAdES-BES signing, SRI submission, RIDE generation, and per-tenant
  certificate/API-key management.

### Fixed
- CI workflow no longer assumes a `billing/` monorepo subdirectory.
- Docker image labels now use the `Qora Billing API` title.

[Unreleased]: https://github.com/JJQuispillo/SriYa/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/JJQuispillo/SriYa/releases/tag/v1.0.0
