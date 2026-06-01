using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sri_tax_codes",
                columns: table => new
                {
                    tax_type_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    percentage_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sri_tax_codes", x => new { x.tax_type_code, x.percentage_code });
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ruc = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    business_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    email_provider = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    smtp_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: true),
                    smtp_user = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_password = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    use_ssl = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sender_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sender_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    access_key = table.Column<string>(type: "character varying(49)", maxLength: 49, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    xml_content = table.Column<string>(type: "text", nullable: true),
                    signed_xml_content = table.Column<string>(type: "text", nullable: true),
                    sri_authorization_number = table.Column<string>(type: "character varying(49)", maxLength: 49, nullable: true),
                    sri_authorization_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    issuer_info = table.Column<string>(type: "jsonb", nullable: false),
                    buyer_info = table.Column<string>(type: "jsonb", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "electronic_signatures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    certificate_data = table.Column<string>(type: "text", nullable: false),
                    password_encrypted = table.Column<string>(type: "varchar(500)", nullable: false),
                    owner_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_electronic_signatures", x => x.id);
                    table.ForeignKey(
                        name: "FK_electronic_signatures_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_destinatarios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identificacion_destinatario = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    razon_social_destinatario = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    dir_destinatario = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    motivo_traslado = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ruta_entrega = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    doc_aduanero_unico = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    cod_estab_destino = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    ruc_transportista = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    rise = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_destinatarios", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_destinatarios_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_events_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    main_code = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    auxiliary_code = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    discount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    tax_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    tax_percentage_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    sustento_document_type = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    sustento_document_number = table.Column<string>(type: "character varying(39)", maxLength: 39, nullable: true),
                    sustento_document_issue_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sustento_document_auth_number = table.Column<string>(type: "character varying(49)", maxLength: 49, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_items_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_destinatario_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    destinatario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    codigo_interno = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    descripcion_detalle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    cantidad_detalle = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_destinatario_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_destinatario_items_document_destinatarios_destinat~",
                        column: x => x.destinatario_id,
                        principalTable: "document_destinatarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "is_active", "rate" },
                values: new object[,]
                {
                    { "303", "1", "Ret. Renta 1%", true, 1m },
                    { "304", "1", "Ret. Renta 1.75%", true, 1.75m },
                    { "312", "1", "Ret. Renta 2%", true, 2m },
                    { "322", "1", "Ret. Renta 8%", true, 8m },
                    { "332", "1", "Ret. Renta 10%", true, 10m },
                    { "343", "1", "Ret. Renta 30%", true, 30m },
                    { "0", "2", "IVA 0%", true, 0m }
                });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "rate" },
                values: new object[] { "10", "2", "IVA 10% (histórico)", 10m });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "is_active", "rate" },
                values: new object[] { "2", "2", "IVA 12%", true, 12m });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "rate" },
                values: new object[] { "3", "2", "IVA 14% (histórico)", 14m });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "is_active", "rate" },
                values: new object[,]
                {
                    { "4", "2", "IVA 15%", true, 15m },
                    { "5", "2", "IVA 5%", true, 5m },
                    { "6", "2", "No Objeto de IVA", true, 0m },
                    { "7", "2", "Exento de IVA", true, 0m }
                });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "rate" },
                values: new object[] { "8", "2", "IVA 8% (histórico)", 8m });

            migrationBuilder.InsertData(
                table: "sri_tax_codes",
                columns: new[] { "percentage_code", "tax_type_code", "description", "is_active", "rate" },
                values: new object[,]
                {
                    { "3011", "3", "ICE Cigarrillos", true, 75m },
                    { "3023", "3", "ICE Cerveza (L)", true, 0.15m },
                    { "3041", "3", "ICE Vehículos < $20k", true, 35m },
                    { "3072", "3", "ICE Bebidas alcohólicas", true, 9m },
                    { "5001", "5", "IRBPNR", true, 0.02m },
                    { "6001", "6", "ISD 5%", true, 5m }
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_tenant_id",
                table: "api_keys",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_destinatario_items_destinatario_id",
                table: "document_destinatario_items",
                column: "destinatario_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_destinatarios_document_id",
                table: "document_destinatarios",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_events_document_id",
                table: "document_events",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_events_tenant_id",
                table: "document_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_items_document_id",
                table: "document_items",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_access_key",
                table: "documents",
                column: "access_key",
                unique: true,
                filter: "access_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_documents_tenant_id_status",
                table: "documents",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_electronic_signatures_tenant_id",
                table: "electronic_signatures",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_ruc",
                table: "tenants",
                column: "ruc",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "document_destinatario_items");

            migrationBuilder.DropTable(
                name: "document_events");

            migrationBuilder.DropTable(
                name: "document_items");

            migrationBuilder.DropTable(
                name: "electronic_signatures");

            migrationBuilder.DropTable(
                name: "sri_tax_codes");

            migrationBuilder.DropTable(
                name: "document_destinatarios");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
