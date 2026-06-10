# Migraciones operacionales

Este directorio contiene scripts y notas para aplicar migraciones de la base
de datos que **no se pueden ejecutar con la transacción por defecto de EF Core**
o que requieren un procedimiento operacional especial (ventana de
mantenimiento, cero downtime, etc.).

## Cuándo usar un script operacional vs. `dotnet ef database update`

| Escenario | Método recomendado |
|-----------|---------------------|
| Migración normal de EF (ADD COLUMN, FK, etc.) | `dotnet ef database update` (lo hace automáticamente al arrancar la app) |
| `CREATE INDEX CONCURRENTLY` / `DROP INDEX CONCURRENTLY` (cero downtime) | Script operacional — `CONCURRENTLY` es incompatible con la transacción de EF |
| DDL sobre objetos multi-tenant que requieren RLS deshabilitado temporalmente | Script operacional con `psql` directo |
| Datos de seed / corrección de datos one-shot | Script operacional con `psql` directo |

## Modos de aplicación soportados por los scripts de este directorio

Cada script (ver abajo) soporta uno o más de los siguientes modos:

1. **`dotnet ef database update` con `--migration <name>`** (default)
   - Usa el runner de EF Core. Puede NO honrar `suppressTransaction: true`
     en algunas combinaciones de provider/versión — si falla con un error de
     tipo `"CREATE INDEX CONCURRENTLY cannot run inside a transaction block"`,
     caer al modo `psql`.
2. **psql directo con el SQL crudo**
   - Aplica el SQL manualmente. Es la opción más segura para DDL que NO
     puede envolverse en transacción. Idempotencia vía `IF NOT EXISTS`.
3. **`--check-only`** (cuando esté implementado)
   - Solo valida pre-flight (existencia de la migración en el árbol,
     variables de entorno, herramientas en PATH). No aplica nada.

## Variables de entorno

- `BILLING_DB_CONNECTION_STRING`: cadena de conexión del **rol propietario**
  de la base de datos. La app corre como `billing_app` (sin permisos DDL),
  por lo que las migraciones operacionales DEBEN correr como owner
  (típicamente `billing_owner` o `postgres`).

  ⚠️ **Nunca** commitear esta variable. Inyectar via `.env`, Vault, o el
  secreto del pipeline.

## Scripts disponibles

| Script | Migración | Modos |
|--------|-----------|-------|
| [`apply-B6-EmissionAtomicity.sh`](./apply-B6-EmissionAtomicity.sh) | `B6_EmissionAtomicity` (2 índices parciales) | `--ef` (default), `--psql`, `--check-only` |

## Procedimiento operativo recomendado

Para una migración operacional como B6:

1. **Notificar** al equipo (canal #deploys): ventana de aplicación.
2. **Backup** de la base (snapshot o `pg_dump`).
3. **Pre-flight check**:
   ```bash
   export BILLING_DB_CONNECTION_STRING="..."
   ./scripts/migrations/apply-B6-EmissionAtomicity.sh --check-only
   ```
4. **Aplicar**:
   ```bash
   ./scripts/migrations/apply-B6-EmissionAtomicity.sh           # modo EF
   # o
   ./scripts/migrations/apply-B6-EmissionAtomicity.sh --psql    # modo psql (fallback)
   ```
5. **Verificar** (con psql):
   ```sql
   SELECT indexname, indexdef FROM pg_indexes
   WHERE tablename = 'documents'
     AND indexname IN ('ix_documents_tenant_secuencial', 'ix_documents_senttosri_createdat');
   ```
   Debe devolver 2 filas.
6. **Confirmar zero-downtime** (no `ACCESS EXCLUSIVE` durante el build):
   ```sql
   SELECT * FROM pg_locks WHERE mode = 'AccessExclusiveLock' AND relation = 'documents'::regclass;
   ```
   Esta query debe devolver 0 filas AHORA. Durante el build, `CONCURRENTLY`
   toma locks más débiles (ShareUpdateExclusiveLock) que NO bloquean reads/writes.
7. **Smoke test** de la API (POST /documentos → 200/422 esperado).
8. **Rollback** (si es necesario, ver runbook de la migración):
   ```sql
   DROP INDEX CONCURRENTLY IF EXISTS "ix_documents_senttosri_createdat";
   DROP INDEX CONCURRENTLY IF EXISTS "ix_documents_tenant_secuencial";
   ```

## Por qué los scripts están en `scripts/` y no en `db/`

Convención del proyecto: los scripts que el operador ejecuta manualmente en
producción viven en `scripts/` (siguiendo el patrón del `install.sh` raíz).
Los archivos de migración de EF viven en
`src/Qora.Billing.Infrastructure/Migrations/` (generados por el modelo C#).
