# distribution Specification (sriyactl)

## Purpose

Distribución de `sriyactl` vía Homebrew bajo el owner `JJQuispillo`: el binario se instala con `brew install JJQuispillo/tap/sriyactl`, los releases se disparan por tag `v*` vía goreleaser con checksums verificables, y `install.sh` queda como un shim delgado que instala el binario y luego ejecuta `sriyactl infra install`.

## Requirements

### Requirement: instalación vía Homebrew tap

`sriyactl` MUST ser instalable con `brew install JJQuispillo/tap/sriyactl`. Tras la instalación, `sriyactl --version` MUST responder con la versión publicada y exit code 0.

#### Scenario: instalación brew exitosa

- GIVEN el tap `JJQuispillo/homebrew-tap` con la fórmula publicada (no draft)
- WHEN el usuario ejecuta `brew install JJQuispillo/tap/sriyactl`
- THEN MUST instalar el binario y `sriyactl --version` MUST imprimir la versión con exit code 0

### Requirement: ownership JJQuispillo (rename desde anomalyco)

El proyecto MUST usar `JJQuispillo` como owner en todos los puntos de distribución: el module path de `go.mod`, los imports internos, y `.goreleaser.yaml` (`tap.owner`, `homepage`, `release.github.owner`). MUST NOT quedar ninguna referencia a `anomalyco`. Tras el rename, `go build ./...` y `go test ./...` MUST pasar.

#### Scenario: sin referencias a anomalyco y build verde

- GIVEN el repo renombrado a `github.com/JJQuispillo/sriyactl`
- WHEN se ejecuta `go build ./... && go test ./...`
- THEN ambos MUST pasar (exit code 0)
- AND una búsqueda de `anomalyco` en el repo MUST NOT devolver coincidencias

### Requirement: release tag-triggered con checksums

Un push de tag `v*` MUST disparar `goreleaser release --clean`, produciendo binarios multi-arch, un `checksums.txt` y publicando la fórmula en el tap. El release MUST NOT ser draft (`release.draft` MUST estar en `false`). Plataformas no soportadas (Windows/arm64 según proposal) MUST quedar `ignore` en la matriz.

#### Scenario: tag dispara release publicado

- GIVEN el workflow de release configurado y un PAT con escritura al tap
- WHEN se pushea un tag `v1.4.0`
- THEN MUST ejecutar goreleaser, generar `checksums.txt` y publicar la fórmula en `JJQuispillo/homebrew-tap`
- AND el release MUST quedar publicado (no draft)

#### Scenario: plataforma ignorada no rompe el release

- GIVEN la matriz con Windows/arm64 marcados `ignore`
- WHEN corre el release
- THEN MUST omitir esos targets sin fallar el pipeline

### Requirement: install.sh es un shim delgado

`install.sh` MUST reducirse a un shim que instale `sriyactl` (vía brew si está disponible, o descargando el binario y verificándolo contra `checksums.txt`) y luego ejecute `exec sriyactl infra install`. El shim MUST verificar el checksum del binario descargado antes de ejecutarlo, y MUST rehusar con guía clara en OS/arch no soportados sin ejecutar un binario corrupto.

#### Scenario: shim instala y delega en infra install

- GIVEN un host soportado con `curl`
- WHEN el usuario ejecuta `install.sh`
- THEN MUST instalar `sriyactl` (brew o binario verificado por checksum) y luego `exec sriyactl infra install`
- AND el flujo end-to-end MUST dejar el stack healthy con exit code 0

#### Scenario: checksum no coincide → aborta

- GIVEN un binario descargado cuyo hash NO coincide con `checksums.txt`
- WHEN el shim verifica el checksum
- THEN MUST abortar con error de integridad y exit code distinto de 0, SIN ejecutar el binario

#### Scenario: OS/arch no soportado

- GIVEN un host con arch no soportada (p.ej. arm de Windows)
- WHEN se ejecuta `install.sh`
- THEN MUST rehusar con un mensaje de guía y exit code distinto de 0, sin descargar ni ejecutar nada
