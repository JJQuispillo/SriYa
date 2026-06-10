# Runbook — sri-emision-atomicidad

Runbook operacional para el change `sri-emision-atomicidad`.

> **Audience**: SRE / DevOps / on-call engineer.
> **Scope**: migración B6 (índices additive, cero downtime) y consideraciones
> operacionales del runtime de PR #2 (handler, retry service, reconciliador).

---

## 1. Migración B6 — `B6_EmissionAtomicity`

Migración **operacional** (NO se aplica automáticamente al arrancar la app —
debe ser disparada por un operador en una ventana separada).

### 1.1 Qué hace

Añade 2 índices parciales sobre `documents`:

| Índice | Columnas | Filtro | Respalda |
|--------|----------|--------|----------|
| `ix_documents_tenant_secuencial` | `(tenant_id, document_type, estab, pto_emision, secuencial DESC)` | `WHERE estab/pto_emision/secuencial IS NOT NULL` | Lock pesimista `MAX(secuencial) FOR UPDATE` del pre-reservation |
| `ix_documents_senttosri_createdat` | `(created_at)` | `WHERE status = 'SentToSri'` | Sweep del reconciliador |

Cero modificaciones a columnas, constraints o datos existentes
(REQ-EMI-028: estrictamente additive). NO se modifica la tabla
`document_events` ni ninguna otra.

### 1.2 Por qué es operacional (no automática)

`CREATE INDEX CONCURRENTLY` es **incompatible con la transacción por defecto
de EF Core**. El runner que llama `Database.MigrateAsync()` al arranque de
la app envuelve cada migración en una transacción, y Postgres rechaza
`CREATE INDEX CONCURRENTLY` dentro de una transacción. Por eso la migración
se aplica con `MigrationBuilder.Sql(..., suppressTransaction: true)` y
debe ser ejecutada FUERA del flujo automático de arranque.

### 1.3 Pre-flight checklist

Antes de aplicar, validar:

- [ ] **Postgres ≥ 11** (soportado por la infra actual; `CONCURRENTLY` existe
      desde 9.x pero la infra está en 16).
- [ ] **Rol propietario** disponible (no `billing_app` — la app no tiene
      permisos DDL sobre `documents`). El owner de la tabla es quien
      ejecuta `CREATE INDEX`.
- [ ] **Backup reciente** de la base (snapshot o `pg_dump`). Aunque
      `CONCURRENTLY` es seguro, el backup es buena práctica antes de
      cualquier DDL masivo.
- [ ] **Ventana de baja concurrencia** identificada (idealmente
      < 100 RPS hacia `POST /documentos`). El build de los índices consume
      I/O y CPU; sin ser bloqueante, sí compite por recursos.
- [ ] **Tamaño de la tabla** registrado (para estimar duración del build):
      ```sql
      SELECT pg_size_pretty(pg_total_relation_size('documents')) AS total_size,
             pg_size_pretty(pg_relation_size('documents'))     AS heap_size,
             (SELECT count(*) FROM documents)                   AS row_count;
      ```
      Como regla general: ~1-2 minutos por millón de filas en hardware
      típico (SSD, 4 vCPU). Multiplicar por 2 índices = total estimado.
- [ ] **Lock state actual** registrado (para detectar regresiones):
      ```sql
      SELECT mode, count(*) FROM pg_locks
      WHERE relation = 'documents'::regclass OR relation = 'document_events'::regclass
      GROUP BY mode;
      ```
      Guardar el output para comparar después.

### 1.4 Apply — modo 1: dotnet ef (default)

```bash
export BILLING_DB_CONNECTION_STRING="Host=db;Port=5432;Database=billing;Username=billing_owner;Password=***"

# Pre-flight
./scripts/migrations/apply-B6-EmissionAtomicity.sh --check-only

# Apply
./scripts/migrations/apply-B6-EmissionAtomicity.sh
```

Si el modo `--ef` falla con
`"CREATE INDEX CONCURRENTLY cannot run inside a transaction block"`, el
provider EF de la versión instalada no honró `suppressTransaction: true`.
Caer al modo 2 (psql directo, abajo).

### 1.5 Apply — modo 2: psql directo (fallback)

```bash
export PGPASSWORD="***"
export BILLING_DB_CONNECTION_STRING="Host=db;Port=5432;Database=billing;Username=billing_owner;Password=***"

./scripts/migrations/apply-B6-EmissionAtomicity.sh --psql
```

El script extrae la connection string, conecta con psql, y aplica los
dos `CREATE INDEX CONCURRENTLY IF NOT EXISTS` directamente.

### 1.6 Apply — modo 3: SQL crudo manual (último recurso)

Si ningún script está disponible, ejecutar con psql desde el host del owner:

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_documents_tenant_secuencial"
    ON "documents" ("tenant_id", "document_type", "estab", "pto_emision", "secuencial" DESC)
    WHERE "estab" IS NOT NULL AND "pto_emision" IS NOT NULL AND "secuencial" IS NOT NULL;

CREATE INDEX CONCURRENTLY IF NOT EXISTS "ix_documents_senttosri_createdat"
    ON "documents" ("created_at")
    WHERE "status" = 'SentToSri';
```

### 1.7 Verificación post-apply

Ejecutar como owner (psql):

```sql
-- 1) Ambos índices presentes
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'documents'
  AND indexname IN ('ix_documents_tenant_secuencial', 'ix_documents_senttosri_createdat')
ORDER BY indexname;
```

**Esperado**: 2 filas.

```sql
-- 2) Índices NO están en estado INVALID (build no falló a mitad de camino)
SELECT indexrelid::regclass AS index_name, indisvalid, indisready
FROM pg_index
WHERE indexrelid::regclass::text IN ('ix_documents_tenant_secuencial', 'ix_documents_senttosri_createdat');
```

**Esperado**: `indisvalid = true` y `indisready = false` para ambos.

```sql
-- 3) No quedó ningún AccessExclusiveLock sostenido
SELECT * FROM pg_locks
WHERE mode = 'AccessExclusiveLock'
  AND relation = 'documents'::regclass;
```

**Esperado**: 0 filas.

```sql
-- 4) Smoke test del query del reconciliador (plan usa el índice nuevo)
EXPLAIN (ANALYZE, BUFFERS)
SELECT id FROM documents
WHERE status = 'SentToSri' AND created_at < now() - interval '10 minutes'
ORDER BY created_at ASC
LIMIT 50;
```

**Esperado**: el plan debe mencionar `Index Scan using
ix_documents_senttosri_createdat` (NO `Seq Scan`).

### 1.8 Rollback

Si la aplicación de B6 causa regresión (poco probable — es additive, no
modifica queries existentes), revertir con:

```sql
DROP INDEX CONCURRENTLY IF EXISTS "ix_documents_senttosri_createdat";
DROP INDEX CONCURRENTLY IF EXISTS "ix_documents_tenant_secuencial";
```

⚠️ NO ejecutar `DROP INDEX` (sin CONCURRENTLY) en producción: tomaría un
`ACCESS EXCLUSIVE` lock sobre `documents` y bloquearía la API hasta que
el drop termine (minutos en tablas grandes).

Después del rollback, hacer un deploy de la app con el código de PR #2
**NO** recomendado (las queries que esperan los índices serían lentas).
Coordinar con el equipo de desarrollo antes de revertir.

### 1.9 Monitoreo post-apply

Después de aplicar, los siguientes queries deben ser eficientes:

```sql
-- Reconciliador (sweep cada 120s por defecto)
EXPLAIN (ANALYZE) SELECT * FROM documents
WHERE status = 'SentToSri' AND created_at < now() - interval '10 minutes'
ORDER BY created_at ASC LIMIT 50;
-- Esperado: Index Scan on ix_documents_senttosri_createdat.

-- Pre-reservation (lock pesimista, ejecutado en cada POST /documentos)
EXPLAIN (ANALYZE) SELECT MAX(secuencial) FROM documents
WHERE tenant_id = $1 AND document_type = $2 AND estab = $3 AND pto_emision = $4
FOR UPDATE;
-- Esperado: Index Only Scan (o Index Scan) sobre ix_documents_tenant_secuencial.
```

Métricas a observar en Grafana/Datadog durante las primeras 24h:

- p99 latencia de `POST /documentos` (debe subir <50ms; el `SELECT ... FOR
  UPDATE` añade ~5-15ms).
- Conteo de `EmissionPreReserved` (EventId 2001) — debe ser ≈ 1 por POST
  exitoso.
- Conteo de `EmissionMonotonicityViolation` (EventId 2004) — esperado ≈ 0
  en operación normal; un pico sugiere race en la generación de
  `secuencial` cliente.
- Conteo de `EmissionReconciliationSweep` (EventId 2020) — debe ser ≈ 1
  cada 120s; >1 sugiere que el sweep está encontrando documentos
  varados.

---

## 2. Runtime — feature flag `AtomicityEnabled` (PR #2)

PR #2 introduce el flag `Sri:Emission:AtomicityEnabled` (default `true`).
Este flag controla SOLO el handler `IssueDocumentAsync` — el FIX N1
(`SriRetryService` persiste) y el `SriReconciliationService` son
**incondicionales** (por diseño D9.a: el reconciliador es la red de
seguridad para N1, apagarlo escondería el bug que defiende).

### 2.1 Rollback del runtime sin redeploy

Si el runtime de PR #2 causa regresión en producción:

1. Setear `Sri:Emission:AtomicityEnabled=false` en `appsettings.json` /
   variable de entorno del pod.
2. Reiniciar el pod (rolling restart).
3. Verificar logs: el handler ahora ejecuta el "legacy flow" (un solo
   `SaveChangesAsync` al final, sin pre-reservation, sin validación de
   monotonía). Los logs de `EmissionPreReserved` y
   `EmissionMonotonicityViolation` deben caer a 0.
4. **NO** desactivar el reconciliador — sigue siendo necesario para
   recuperar `SentToSri` varados. Si se necesita apagarlo
   temporalmente: `Sri:Reconciliation:SweepIntervalSeconds=86400`.

### 2.2 Canary metrics (PR #2)

Durante el canary (10% → 100%), observar:

- p99 emisión < 50ms por encima del baseline
- Conteo de `SentToSri` > 10min debe tender a 0
- Conteo de `DuplicateBusinessIdentityException` (422)
- Conteo de `"Certificado expiró durante reintento"` (EventId 2011)
- Conteo de `"EmissionCircuitOpenCaught"` (EventId 2005)
- Conteo de `"EmissionReconciliationSweep"` (EventId 2020)

---

## 3. Comandos rápidos (cheat sheet)

```bash
# Estado de los índices
psql -c "SELECT indexname, pg_size_pretty(pg_relation_size(indexname::regclass)) FROM pg_indexes WHERE tablename = 'documents' ORDER BY indexname;"

# Plan del reconciliador
psql -c "EXPLAIN (ANALYZE) SELECT id FROM documents WHERE status = 'SentToSri' AND created_at < now() - interval '10 minutes' ORDER BY created_at ASC LIMIT 50;"

# Plan del pre-reservation
psql -c "EXPLAIN (ANALYZE) SELECT MAX(secuencial) FROM documents WHERE tenant_id = '...' AND document_type = '...' AND estab = '...' AND pto_emision = '...' FOR UPDATE;"

# Locks activos sobre documents
psql -c "SELECT * FROM pg_locks WHERE relation = 'documents'::regclass;"

# SentToSri varados (debería ser ~0)
psql -c "SELECT count(*) FROM documents WHERE status = 'SentToSri' AND created_at < now() - interval '10 minutes';"
```
