using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5StockAndReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "stock_controlled",
                schema: "tpv",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "stock_minimum",
                schema: "tpv",
                table: "products",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "stock_quantity",
                schema: "tpv",
                table: "products",
                type: "numeric(12,3)",
                precision: 12,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "stock_unit",
                schema: "tpv",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "tpv",
                table: "products",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "app_settings",
                schema: "tpv",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    product_id = table.Column<int>(type: "integer", nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    quantity_after = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    reference_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_delta", "quantity_delta <> 0");
                    table.ForeignKey(
                        name: "fk_stock_movements_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "tpv",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_stock_minimum",
                schema: "tpv",
                table: "products",
                sql: "stock_minimum >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_product_date",
                schema: "tpv",
                table: "stock_movements",
                columns: new[] { "product_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_movements_reference",
                schema: "tpv",
                table: "stock_movements",
                columns: new[] { "reason", "reference_type", "reference_id" },
                unique: true,
                filter: "reference_id IS NOT NULL");

            // Los movimientos de inventario son inmutables (misma función que los de dinero): las correcciones
            // se hacen con otro movimiento (entrada, salida o ajuste), nunca editando el histórico.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_stock_movements_immutable BEFORE UPDATE OR DELETE ON tpv.stock_movements
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_stock_movements_no_truncate BEFORE TRUNCATE ON tpv.stock_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_stock_movements_no_truncate ON tpv.stock_movements;
                DROP TRIGGER IF EXISTS trg_stock_movements_immutable ON tpv.stock_movements;
                """);

            migrationBuilder.DropTable(
                name: "app_settings",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "stock_movements",
                schema: "tpv");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_stock_minimum",
                schema: "tpv",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock_controlled",
                schema: "tpv",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock_minimum",
                schema: "tpv",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock_quantity",
                schema: "tpv",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock_unit",
                schema: "tpv",
                table: "products");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "tpv",
                table: "products");
        }
    }
}
