using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2SalesCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    operation = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_rates",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rates", x => x.id);
                    table.CheckConstraint("ck_tax_rates_rate", "rate >= 0 AND rate <= 100");
                });

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zones", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    tax_rate_id = table.Column<int>(type: "integer", nullable: false),
                    sku = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sale_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    station = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_price", "sale_price >= 0");
                    table.ForeignKey(
                        name: "fk_products_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "tpv",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_tax_rates_tax_rate_id",
                        column: x => x.tax_rate_id,
                        principalSchema: "tpv",
                        principalTable: "tax_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dining_tables",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    zone_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dining_tables", x => x.id);
                    table.CheckConstraint("ck_dining_tables_capacity", "capacity > 0");
                    table.ForeignKey(
                        name: "fk_dining_tables_zones_zone_id",
                        column: x => x.zone_id,
                        principalSchema: "tpv",
                        principalTable: "zones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    table_id = table.Column<int>(type: "integer", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    guest_count = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_closed", "(status = 'Open' AND closed_at IS NULL) OR status <> 'Open'");
                    table.CheckConstraint("ck_orders_guest_count", "guest_count > 0");
                    table.CheckConstraint("ck_orders_total_matches", "grand_total = subtotal + tax_total");
                    table.CheckConstraint("ck_orders_totals", "subtotal >= 0 AND discount_total >= 0 AND tax_total >= 0 AND grand_total >= 0");
                    table.ForeignKey(
                        name: "fk_orders_dining_tables_table_id",
                        column: x => x.table_id,
                        principalSchema: "tpv",
                        principalTable: "dining_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kitchen_tickets",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    station = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    table_label = table.Column<string>(type: "character varying(130)", maxLength: 130, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_kitchen_tickets_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "tpv",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<int>(type: "integer", nullable: true),
                    product_name_snapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_rate_snapshot = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    station = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.CheckConstraint("ck_order_lines_price", "unit_price >= 0");
                    table.CheckConstraint("ck_order_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_order_lines_total", "line_total >= 0");
                    table.ForeignKey(
                        name: "fk_order_lines_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "tpv",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_lines_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "tpv",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kitchen_ticket_lines",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kitchen_ticket_id = table.Column<long>(type: "bigint", nullable: false),
                    order_line_id = table.Column<long>(type: "bigint", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    product_name_snapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    notes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_ticket_lines", x => x.id);
                    table.CheckConstraint("ck_kitchen_ticket_lines_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_lines_kitchen_tickets_kitchen_ticket_id",
                        column: x => x.kitchen_ticket_id,
                        principalSchema: "tpv",
                        principalTable: "kitchen_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_lines_order_lines_order_line_id",
                        column: x => x.order_line_id,
                        principalSchema: "tpv",
                        principalTable: "order_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_categories_name",
                schema: "tpv",
                table: "categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dining_tables_zone_name",
                schema: "tpv",
                table: "dining_tables",
                columns: new[] { "zone_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_key",
                schema: "tpv",
                table: "idempotency_records",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_lines_kitchen_ticket_id",
                schema: "tpv",
                table: "kitchen_ticket_lines",
                column: "kitchen_ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_lines_order_line_id",
                schema: "tpv",
                table: "kitchen_ticket_lines",
                column: "order_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_tickets_order_id",
                schema: "tpv",
                table: "kitchen_tickets",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_tickets_status",
                schema: "tpv",
                table: "kitchen_tickets",
                columns: new[] { "status", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_order",
                schema: "tpv",
                table: "order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_product_id",
                schema: "tpv",
                table: "order_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_opened_at",
                schema: "tpv",
                table: "orders",
                column: "opened_at");

            migrationBuilder.CreateIndex(
                name: "ix_orders_status",
                schema: "tpv",
                table: "orders",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_orders_one_live_per_table",
                schema: "tpv",
                table: "orders",
                column: "table_id",
                unique: true,
                filter: "status IN ('Open','PendingPayment')");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_active",
                schema: "tpv",
                table: "products",
                columns: new[] { "category_id", "is_active", "display_order" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tax_rate_id",
                schema: "tpv",
                table: "products",
                column: "tax_rate_id");

            migrationBuilder.CreateIndex(
                name: "ux_products_sku",
                schema: "tpv",
                table: "products",
                column: "sku",
                unique: true,
                filter: "sku IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_zones_name",
                schema: "tpv",
                table: "zones",
                column: "name",
                unique: true);

            // Permisos nuevos para roles que ya existían en la base de datos (el seed de referencia solo
            // asigna permisos al crear un rol). En una base nueva aún no hay roles: no hace nada y el seed
            // los crea con la matriz actual.
            migrationBuilder.Sql("""
                INSERT INTO tpv.role_permissions (role_id, permission)
                SELECT r.id, v.permission
                FROM tpv.roles r
                JOIN (VALUES
                    ('Administrador', 'ManageCatalog'), ('Encargado', 'ManageCatalog'),
                    ('Administrador', 'TakeOrders'), ('Encargado', 'TakeOrders'),
                    ('Camarero', 'TakeOrders'), ('Caja', 'TakeOrders')
                ) AS v(role_name, permission) ON v.role_name = r.name
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM tpv.role_permissions WHERE permission IN ('ManageCatalog', 'TakeOrders');");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "kitchen_ticket_lines",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "kitchen_tickets",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "products",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "dining_tables",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "tax_rates",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "tpv");
        }
    }
}
