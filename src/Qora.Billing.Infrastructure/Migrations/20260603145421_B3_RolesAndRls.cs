using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <summary>
    /// Defensa en profundidad de RLS para multi-tenancy:
    ///   1. Agrega tenant_id desnormalizado (nullable) a las 3 tablas hijas, lo rellena desde el padre,
    ///      crea un trigger BEFORE INSERT que lo deriva del padre (cinturón), y luego lo marca NOT NULL.
    ///   2. ENABLE + FORCE ROW LEVEL SECURITY en las 7 tablas con alcance de tenant y crea las políticas
    ///      USING/WITH CHECK basadas en current_setting('app.current_tenant').
    ///
    /// Idempotente: usa IF NOT EXISTS / DROP POLICY IF EXISTS para poder re-ejecutar de forma segura.
    /// current_setting(...,true) devuelve NULL cuando la GUC no está fijada → la comparación es falsa →
    /// 0 filas (fail-closed a nivel de BD). Los caminos cross-tenant usan el rol billing_privileged (BYPASSRLS).
    ///
    /// FORCE ROW LEVEL SECURITY hace que la política aplique incluso al propietario de la tabla, cerrando
    /// el "owner bypass" silencioso. (Los superusuarios siguen evadiendo RLS, por eso la app NO corre como
    /// superusuario sino como billing_app.)
    /// </summary>
    /// <inheritdoc />
    public partial class B3_RolesAndRls : Migration
    {
        // NULLIF(..., '') es clave: una GUC personalizada, tras un SET LOCAL dentro de una transacción,
        // al terminar ésta NO vuelve a "no fijada" (NULL) sino a su valor de sesión, que es la cadena
        // VACÍA ''. Sin el NULLIF, una consulta posterior sin tenant sobre una conexión reutilizada del
        // pool evaluaría ''::uuid y lanzaría 22P02 (invalid input syntax for type uuid). Con NULLIF, ''
        // → NULL → la comparación es falsa → 0 filas (fail-closed), sin error de cast.
        private const string TenantGuc = "NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. tenant_id desnormalizado en tablas hijas ───────────────────────────────

            // document_items y document_destinatarios derivan de documents (FK document_id).
            foreach (var child in new[] { "document_items", "document_destinatarios" })
            {
                migrationBuilder.Sql($@"
                    ALTER TABLE {child} ADD COLUMN IF NOT EXISTS tenant_id uuid;
                    UPDATE {child} c
                    SET tenant_id = d.tenant_id
                    FROM documents d
                    WHERE c.document_id = d.id AND c.tenant_id IS NULL;
                ");
            }

            // document_destinatario_items deriva de document_destinatarios (FK destinatario_id),
            // que a su vez ya tiene tenant_id tras el paso anterior.
            migrationBuilder.Sql(@"
                ALTER TABLE document_destinatario_items ADD COLUMN IF NOT EXISTS tenant_id uuid;
                UPDATE document_destinatario_items i
                SET tenant_id = des.tenant_id
                FROM document_destinatarios des
                WHERE i.destinatario_id = des.id AND i.tenant_id IS NULL;
            ");

            // ── 2. Triggers BEFORE INSERT que derivan tenant_id del padre (cinturón) ───────

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_set_tenant_from_document()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.tenant_id IS NULL THEN
                        SELECT d.tenant_id INTO NEW.tenant_id
                        FROM documents d WHERE d.id = NEW.document_id;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION fn_set_tenant_from_destinatario()
                RETURNS trigger AS $$
                BEGIN
                    IF NEW.tenant_id IS NULL THEN
                        SELECT des.tenant_id INTO NEW.tenant_id
                        FROM document_destinatarios des WHERE des.id = NEW.destinatario_id;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_document_items_tenant ON document_items;
                CREATE TRIGGER trg_document_items_tenant
                    BEFORE INSERT ON document_items
                    FOR EACH ROW EXECUTE FUNCTION fn_set_tenant_from_document();

                DROP TRIGGER IF EXISTS trg_document_destinatarios_tenant ON document_destinatarios;
                CREATE TRIGGER trg_document_destinatarios_tenant
                    BEFORE INSERT ON document_destinatarios
                    FOR EACH ROW EXECUTE FUNCTION fn_set_tenant_from_document();

                DROP TRIGGER IF EXISTS trg_document_destinatario_items_tenant ON document_destinatario_items;
                CREATE TRIGGER trg_document_destinatario_items_tenant
                    BEFORE INSERT ON document_destinatario_items
                    FOR EACH ROW EXECUTE FUNCTION fn_set_tenant_from_destinatario();
            ");

            // ── 3. NOT NULL + índice de tenant en las hijas ───────────────────────────────
            migrationBuilder.Sql(@"
                ALTER TABLE document_items ALTER COLUMN tenant_id SET NOT NULL;
                ALTER TABLE document_destinatarios ALTER COLUMN tenant_id SET NOT NULL;
                ALTER TABLE document_destinatario_items ALTER COLUMN tenant_id SET NOT NULL;

                CREATE INDEX IF NOT EXISTS ix_document_items_tenant_id ON document_items (tenant_id);
                CREATE INDEX IF NOT EXISTS ix_document_destinatarios_tenant_id ON document_destinatarios (tenant_id);
                CREATE INDEX IF NOT EXISTS ix_document_destinatario_items_tenant_id ON document_destinatario_items (tenant_id);
            ");

            // ── 4. ENABLE + FORCE RLS + políticas en las 7 tablas con alcance de tenant ────
            var tenantTables = new[]
            {
                "documents",
                "api_keys",
                "electronic_signatures",
                "document_events",
                "document_items",
                "document_destinatarios",
                "document_destinatario_items",
            };

            foreach (var table in tenantTables)
            {
                migrationBuilder.Sql($@"
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS p_{table}_tenant ON {table};
                    CREATE POLICY p_{table}_tenant ON {table}
                        USING (tenant_id = {TenantGuc})
                        WITH CHECK (tenant_id = {TenantGuc});
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var tenantTables = new[]
            {
                "documents",
                "api_keys",
                "electronic_signatures",
                "document_events",
                "document_items",
                "document_destinatarios",
                "document_destinatario_items",
            };

            foreach (var table in tenantTables)
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS p_{table}_tenant ON {table};
                    ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                ");
            }

            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_document_items_tenant ON document_items;
                DROP TRIGGER IF EXISTS trg_document_destinatarios_tenant ON document_destinatarios;
                DROP TRIGGER IF EXISTS trg_document_destinatario_items_tenant ON document_destinatario_items;
                DROP FUNCTION IF EXISTS fn_set_tenant_from_document();
                DROP FUNCTION IF EXISTS fn_set_tenant_from_destinatario();

                DROP INDEX IF EXISTS ix_document_items_tenant_id;
                DROP INDEX IF EXISTS ix_document_destinatarios_tenant_id;
                DROP INDEX IF EXISTS ix_document_destinatario_items_tenant_id;

                ALTER TABLE document_items DROP COLUMN IF EXISTS tenant_id;
                ALTER TABLE document_destinatarios DROP COLUMN IF EXISTS tenant_id;
                ALTER TABLE document_destinatario_items DROP COLUMN IF EXISTS tenant_id;
            ");
        }
    }
}
