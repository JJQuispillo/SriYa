using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Qora.Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSriTaxCodes : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sri_tax_codes");
        }
    }
}
