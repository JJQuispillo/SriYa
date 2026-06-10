using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// Idempotencia (II-1) + dedupe de identidad de negocio (II-2):
    ///   1. Crea la tabla idempotency_keys (tenant_id desnormalizado) y la pone bajo RLS igual que el resto
    ///      de tablas con alcance de tenant (ENABLE+FORCE + política sobre app.current_tenant) más un trigger
    ///      BEFORE INSERT que rellena tenant_id desde la GUC si el app lo omite (cinturón, como las hijas).
    ///   2. DEDUPLICA filas pre-existentes con la misma identidad de negocio (tenant_id, document_type, estab,
    ///      pto_emision, secuencial) ANTES de crear el unique constraint, para no fallar sobre datos sucios
    ///      en despliegues existentes. Las duplicadas se mueven a una tabla de cuarentena
    ///      (documents_business_identity_duplicates) conservando la más antigua (created_at) por grupo.
    ///   3. Crea el índice único parcial ux_documents_business_identity (sólo cuando estab/ptoEmi/secuencial
    ///      NO son NULL — las filas pre-B2 sin identidad no se ven afectadas).
    ///
    /// Idempotente: IF NOT EXISTS / DROP ... IF EXISTS en todo el SQL crudo, seguro de re-ejecutar.
    /// </summary>
    /// <inheritdoc />
    public partial class B4_IdempotencyAndBusinessIdentity : Migration
    {
        // Mismo patrón que B3: NULLIF(...,'')::uuid para sobrevivir a la GUC vacía tras un SET tx-local
        // sobre una conexión reutilizada del pool (evita 22P02 ''::uuid).
        private const string TenantGuc = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Tabla idempotency_keys ─────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    response_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires_at",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_keys_tenant_key",
                table: "idempotency_keys",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            // ── 1b. RLS sobre idempotency_keys (igual que las 7 tablas de B3) ──────────────
            // Trigger BEFORE INSERT: si el app omite tenant_id, lo deriva de la GUC (cinturón). Bajo RLS, la
            // política WITH CHECK además exige que coincida con la GUC, por lo que un tenant no puede insertar
            // claves de otro. El GRANT a billing_app ya lo cubre el GRANT ON ALL TABLES de B3, pero las tablas
            // creadas DESPUÉS de ese GRANT no lo heredan: re-otorgamos explícitamente sobre esta tabla nueva.
            migrationBuilder.Sql($@"
                GRANT SELECT, INSERT, UPDATE, DELETE ON idempotency_keys TO {Persistence.RoleProvisioner.AppRoleName};

                CREATE OR REPLACE FUNCTION fn_set_tenant_from_guc()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.tenant_id IS NULL THEN
                        NEW.tenant_id := {TenantGuc};
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_idempotency_keys_tenant ON idempotency_keys;
                CREATE TRIGGER trg_idempotency_keys_tenant
                    BEFORE INSERT ON idempotency_keys
                    FOR EACH ROW EXECUTE FUNCTION fn_set_tenant_from_guc();

                ALTER TABLE idempotency_keys ENABLE ROW LEVEL SECURITY;
                ALTER TABLE idempotency_keys FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS p_idempotency_keys_tenant ON idempotency_keys;
                CREATE POLICY p_idempotency_keys_tenant ON idempotency_keys
                    USING (tenant_id = {TenantGuc})
                    WITH CHECK (tenant_id = {TenantGuc});
            ");

            // ── 2. DEDUPE pre-existente de identidad de negocio (ANTES del constraint) ─────
            // Se ejecuta como propietario durante MigrateAsync (no hay GUC de tenant) → ve todas las filas.
            // Cuarentena: copia las duplicadas (todas menos la más antigua por grupo) a una tabla aparte
            // para auditoría y las elimina de documents, dejando el conjunto limpio para el unique.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS documents_business_identity_duplicates (
                    id uuid NOT NULL,
                    tenant_id uuid NOT NULL,
                    document_type character varying(30) NOT NULL,
                    estab char(3),
                    pto_emision char(3),
                    secuencial char(9),
                    quarantined_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (id, quarantined_at)
                );

                WITH ranked AS (
                    SELECT id, tenant_id, document_type, estab, pto_emision, secuencial,
                           ROW_NUMBER() OVER (
                               PARTITION BY tenant_id, document_type, estab, pto_emision, secuencial
                               ORDER BY created_at ASC, id ASC
                           ) AS rn
                    FROM documents
                    WHERE estab IS NOT NULL AND pto_emision IS NOT NULL AND secuencial IS NOT NULL
                )
                INSERT INTO documents_business_identity_duplicates
                    (id, tenant_id, document_type, estab, pto_emision, secuencial)
                SELECT id, tenant_id, document_type, estab, pto_emision, secuencial
                FROM ranked
                WHERE rn > 1;

                -- Elimina los hijos de las duplicadas antes de borrar las cabeceras (FK RESTRICT).
                DELETE FROM document_destinatario_items WHERE destinatario_id IN (
                    SELECT des.id FROM document_destinatarios des
                    WHERE des.document_id IN (SELECT id FROM documents_business_identity_duplicates));
                DELETE FROM document_destinatarios WHERE document_id IN (
                    SELECT id FROM documents_business_identity_duplicates);
                DELETE FROM document_items WHERE document_id IN (
                    SELECT id FROM documents_business_identity_duplicates);
                DELETE FROM document_events WHERE document_id IN (
                    SELECT id FROM documents_business_identity_duplicates);
                DELETE FROM documents WHERE id IN (
                    SELECT id FROM documents_business_identity_duplicates);
            ");

            // ── 3. Unique constraint de identidad de negocio (índice único parcial) ────────
            // Parcial: sólo aplica cuando los 3 componentes existen, para no chocar con filas pre-B2.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ux_documents_business_identity
                    ON documents (tenant_id, document_type, estab, pto_emision, secuencial)
                    WHERE estab IS NOT NULL AND pto_emision IS NOT NULL AND secuencial IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ux_documents_business_identity;

                DROP POLICY IF EXISTS p_idempotency_keys_tenant ON idempotency_keys;
                DROP TRIGGER IF EXISTS trg_idempotency_keys_tenant ON idempotency_keys;
                DROP FUNCTION IF EXISTS fn_set_tenant_from_guc();
            ");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            // La tabla de cuarentena se conserva deliberadamente (auditoría); su contenido no se restaura.
        }
    }
}
