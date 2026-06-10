#!/usr/bin/env bash
# ============================================================================
# scripts/migrations/apply-B6-EmissionAtomicity.sh
#
# Aplica la migración B6_EmissionAtomicity de forma OPERACIONAL.
#
# ⚠️  ADVERTENCIA — MIGRACIÓN OPERACIONAL
# Esta migración añade 2 índices parciales a la tabla `documents` con
# `CREATE INDEX CONCURRENTLY` (cero downtime: NO toma un ACCESS EXCLUSIVE
# lock). Se aplica ANTES de desplegar el código de runtime de PR #2, en una
# ventana separada (típicamente 1h antes del canary).
#
# La migración es idempotente (IF NOT EXISTS) y safe to re-run.
#
# Modos soportados (en orden de preferencia):
#   1) `dotnet ef database update` con la migración objetivo (default).
#   2) psql directo con el SQL crudo (si el provider EF no honra
#      `suppressTransaction` para DDL no transaccional en tu instalación).
#
# Requisitos:
#   - dotnet 9.x instalado (para el modo 1)
#   - psql cliente (opcional, modo 2)
#   - Variable de entorno BILLING_DB_CONNECTION_STRING con la cadena
#     de conexión del rol PROPIETARIO (no billing_app — la app no puede
#     ejecutar DDL ni CREATE INDEX CONCURRENTLY sin ser owner de la tabla).
#
# Uso:
#   export BILLING_DB_CONNECTION_STRING="Host=db;Port=5432;Database=billing;Username=billing_owner;Password=..."
#   ./scripts/migrations/apply-B6-EmissionAtomicity.sh                # modo EF (default)
#   ./scripts/migrations/apply-B6-EmissionAtomicity.sh --psql         # modo psql crudo
#   ./scripts/migrations/apply-B6-EmissionAtomicity.sh --check-only   # solo verifica pre-flight
# ============================================================================
set -euo pipefail

readonly MIGRATION_NAME="B6_EmissionAtomicity"
readonly MIGRATION_FILE="src/Qora.Billing.Infrastructure/Migrations/20260604100000_${MIGRATION_NAME}.cs"

# ---- Argumentos --------------------------------------------------------------
MODE="ef"
if [[ $# -gt 0 ]]; then
  case "$1" in
    --psql)        MODE="psql" ;;
    --check-only)  MODE="check" ;;
    --ef)          MODE="ef" ;;
    -h|--help)
      sed -n '2,30p' "$0"
      exit 0
      ;;
    *)
      echo "❌ Argumento desconocido: $1" >&2
      echo "   Usa --ef (default), --psql o --check-only" >&2
      exit 64
      ;;
  esac
fi

# ---- Pre-flight checks -------------------------------------------------------
echo "============================================================"
echo " Migración operacional ${MIGRATION_NAME}"
echo " Modo: ${MODE}"
echo "============================================================"

if [[ -z "${BILLING_DB_CONNECTION_STRING:-}" ]]; then
  echo "❌ ERROR: variable de entorno BILLING_DB_CONNECTION_STRING no está definida." >&2
  echo "   Debe ser la cadena de conexión del rol PROPIETARIO de la DB" >&2
  echo "   (no billing_app — la app no tiene permisos para CREATE INDEX)." >&2
  exit 65
fi

# Redact password para no leakearla en logs.
REDACTED_CS=$(echo "${BILLING_DB_CONNECTION_STRING}" | sed -E 's|(Password|password)=[^;]*|\1=***REDACTED***|gI')
echo "Connection string (redactado): ${REDACTED_CS}"

if [[ "${MODE}" != "check" ]]; then
  if [[ "${MODE}" == "ef" ]] && ! command -v dotnet >/dev/null 2>&1; then
    echo "❌ ERROR: dotnet no está en el PATH (modo --ef)." >&2
    echo "   Usa --psql o instala el SDK .NET 9." >&2
    exit 66
  fi
  if [[ "${MODE}" == "psql" ]] && ! command -v psql >/dev/null 2>&1; then
    echo "❌ ERROR: psql no está en el PATH (modo --psql)." >&2
    echo "   Instala postgresql-client o usa --ef." >&2
    exit 66
  fi
fi

# Verificar que el archivo de migración existe en el árbol de código fuente.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
if [[ ! -f "${REPO_ROOT}/${MIGRATION_FILE}" ]]; then
  echo "❌ ERROR: no se encontró el archivo de migración:" >&2
  echo "   ${REPO_ROOT}/${MIGRATION_FILE}" >&2
  echo "   ¿Estás ejecutando el script desde el repo correcto?" >&2
  exit 67
fi
echo "✓ Archivo de migración presente: ${MIGRATION_FILE}"

# ---- Modo check: solo pre-flight, no aplica nada -----------------------------
if [[ "${MODE}" == "check" ]]; then
  echo "✓ Pre-flight OK (modo --check-only, no se aplicaron cambios)."
  exit 0
fi

# ---- Aplicar ----------------------------------------------------------------
echo ""
echo "Aplicando migración ${MIGRATION_NAME} en modo ${MODE}..."

EXIT_CODE=0
if [[ "${MODE}" == "ef" ]]; then
  # La bandera `--` separa argumentos de dotnet ef de los del tool. El argumento
  # `--migration <name>` indica la migración objetivo (EF Core 9 soporta este
  # overload). Si el provider EF no honra `suppressTransaction: true` para DDL
  # CONCURRENTLY en la versión instalada, el script --psql es el fallback.
  set +e
  dotnet ef database update \
    --project "${REPO_ROOT}/src/Qora.Billing.Infrastructure" \
    --startup-project "${REPO_ROOT}/src/Qora.Billing.Api" \
    --context BillingDbContext \
    --connection "${BILLING_DB_CONNECTION_STRING}" \
    -- --migration "${MIGRATION_NAME}"
  EXIT_CODE=$?
  set -e
elif [[ "${MODE}" == "psql" ]]; then
  # Modo psql crudo: extrae la connection string a sus componentes y aplica
  # los dos CREATE INDEX CONCURRENTLY directamente. Útil si el runner EF no
  # desactiva la transacción para esta migración en particular.
  set +e
  PGPASSWORD="$(echo "${BILLING_DB_CONNECTION_STRING}" | sed -nE 's|.*[Pp]assword=([^;]+).*|\1|p')" \
    psql "${BILLING_DB_CONNECTION_STRING}" -v ON_ERROR_STOP=1 <<'SQL'
-- Idempotente: IF NOT EXISTS permite re-ejecución.
CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_documents_tenant_secuencial"
    ON "documents" ("tenant_id", "document_type", "estab", "pto_emision", "secuencial" DESC)
    WHERE "estab" IS NOT NULL AND "pto_emision" IS NOT NULL AND "secuencial" IS NOT NULL;

CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_documents_senttosri_createdat"
    ON "documents" ("created_at")
    WHERE "status" = 'SentToSri';
SQL
  EXIT_CODE=$?
  set -e
fi

# ---- Verificación post-apply ------------------------------------------------
if [[ ${EXIT_CODE} -eq 0 ]]; then
  echo ""
  echo "✅ Migración ${MIGRATION_NAME} aplicada correctamente."
  echo ""
  echo "Verificación (ejecutar manualmente con psql):"
  echo "  SELECT indexname FROM pg_indexes"
  echo "  WHERE tablename = 'documents'"
  echo "    AND indexname IN ('ix_documents_tenant_secuencial', 'ix_documents_senttosri_createdat');"
  echo ""
  echo "Debe devolver 2 filas. Si devuelve 0, la migración no se aplicó (revisar logs)."
  echo ""
  echo "Verificar que NO se sostuvo un ACCESS EXCLUSIVE lock durante el build:"
  echo "  SELECT * FROM pg_locks WHERE mode = 'AccessExclusiveLock' AND relation = 'documents'::regclass;"
  echo "  (Esta query debe devolver 0 filas AHORA; durante el build pudo haber tomado uno brevemente"
  echo "   por otras operaciones, pero el CREATE INDEX CONCURRENTLY NO lo toma.)"
  exit 0
else
  echo ""
  echo "❌ ERROR: la aplicación de la migración ${MIGRATION_NAME} falló (exit ${EXIT_CODE})." >&2
  echo "" >&2
  echo "Diagnóstico: ejecutar las siguientes queries con psql para inspeccionar el estado:" >&2
  echo "  SELECT pid, query, state, wait_event_type, wait_event FROM pg_stat_activity" >&2
  echo "    WHERE datname = current_database() AND state != 'idle';" >&2
  echo "  SELECT * FROM pg_locks WHERE mode = 'AccessExclusiveLock';" >&2
  echo "  SELECT indexname FROM pg_indexes WHERE tablename = 'documents';" >&2
  echo "" >&2
  echo "Si quedó un índice marcado como INVALID (build falló a mitad de camino)," >&2
  echo "  limpiarlo con: DROP INDEX CONCURRENTLY IF EXISTS \"ix_documents_tenant_secuencial\";" >&2
  echo "                  DROP INDEX CONCURRENTLY IF EXISTS \"ix_documents_senttosri_createdat\";" >&2
  echo "  y re-ejecutar este script (es idempotente)." >&2
  exit ${EXIT_CODE}
fi
