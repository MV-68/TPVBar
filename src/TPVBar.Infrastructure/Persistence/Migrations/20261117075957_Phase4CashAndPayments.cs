using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4CashAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "paid_total",
                schema: "tpv",
                table: "orders",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "cash_sessions",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opening_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    difference = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_sessions", x => x.id);
                    table.CheckConstraint("ck_cash_sessions_close", "(status = 'Open' AND closed_at IS NULL) OR (status = 'Closed' AND closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_cash_sessions_opening", "opening_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "payment_methods",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    requires_reference = table.Column<bool>(type: "boolean", nullable: false),
                    affects_cash_drawer = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_methods", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cash_movements",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    cash_session_id = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    concept = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_movements", x => x.id);
                    table.CheckConstraint("ck_cash_movements_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_cash_movements_cash_sessions_cash_session_id",
                        column: x => x.cash_session_id,
                        principalSchema: "tpv",
                        principalTable: "cash_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    cash_session_id = table.Column<long>(type: "bigint", nullable: false),
                    payment_method_id = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tendered_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    change_given = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    external_reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_payments_change", "change_given >= 0 AND (tendered_amount IS NULL OR tendered_amount - amount = change_given)");
                    table.ForeignKey(
                        name: "fk_payments_cash_sessions_cash_session_id",
                        column: x => x.cash_session_id,
                        principalSchema: "tpv",
                        principalTable: "cash_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "tpv",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_payment_methods_payment_method_id",
                        column: x => x.payment_method_id,
                        principalSchema: "tpv",
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    payment_id = table.Column<long>(type: "bigint", nullable: false),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    cash_session_id = table.Column<long>(type: "bigint", nullable: false),
                    payment_method_id = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_refunds", x => x.id);
                    table.CheckConstraint("ck_payment_refunds_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_payment_refunds_cash_sessions_cash_session_id",
                        column: x => x.cash_session_id,
                        principalSchema: "tpv",
                        principalTable: "cash_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "tpv",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_payment_methods_payment_method_id",
                        column: x => x.payment_method_id,
                        principalSchema: "tpv",
                        principalTable: "payment_methods",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "tpv",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_paid_total",
                schema: "tpv",
                table: "orders",
                sql: "paid_total >= 0 AND paid_total <= grand_total");

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_session",
                schema: "tpv",
                table: "cash_movements",
                column: "cash_session_id");

            migrationBuilder.CreateIndex(
                name: "ux_cash_sessions_one_open",
                schema: "tpv",
                table: "cash_sessions",
                column: "status",
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ux_payment_methods_code",
                schema: "tpv",
                table: "payment_methods",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_order_id",
                schema: "tpv",
                table: "payment_refunds",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_payment",
                schema: "tpv",
                table: "payment_refunds",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_payment_method_id",
                schema: "tpv",
                table: "payment_refunds",
                column: "payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_refunds_session",
                schema: "tpv",
                table: "payment_refunds",
                column: "cash_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_order",
                schema: "tpv",
                table: "payments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_payment_method_id",
                schema: "tpv",
                table: "payments",
                column: "payment_method_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_session_date",
                schema: "tpv",
                table: "payments",
                columns: new[] { "cash_session_id", "paid_at" });

            // El dinero registrado es inmutable: cobros, devoluciones y movimientos de caja no se modifican ni se
            // borran. Las correcciones se hacen con operaciones compensatorias (devolución, movimiento inverso).
            migrationBuilder.Sql("""
                CREATE FUNCTION tpv.prevent_ledger_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% es inmutable: no se permite % (use una operación compensatoria)', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_payments_immutable BEFORE UPDATE OR DELETE ON tpv.payments
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_payments_no_truncate BEFORE TRUNCATE ON tpv.payments
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();

                CREATE TRIGGER trg_payment_refunds_immutable BEFORE UPDATE OR DELETE ON tpv.payment_refunds
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_payment_refunds_no_truncate BEFORE TRUNCATE ON tpv.payment_refunds
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();

                CREATE TRIGGER trg_cash_movements_immutable BEFORE UPDATE OR DELETE ON tpv.cash_movements
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_cash_movements_no_truncate BEFORE TRUNCATE ON tpv.cash_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();
                """);

            // Permiso nuevo para roles que ya existían (el seed solo asigna permisos al crear un rol).
            migrationBuilder.Sql("""
                INSERT INTO tpv.role_permissions (role_id, permission)
                SELECT r.id, v.permission
                FROM tpv.roles r
                JOIN (VALUES
                    ('Administrador', 'TakePayments'), ('Encargado', 'TakePayments'),
                    ('Camarero', 'TakePayments'), ('Caja', 'TakePayments')
                ) AS v(role_name, permission) ON v.role_name = r.name
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_cash_movements_no_truncate ON tpv.cash_movements;
                DROP TRIGGER IF EXISTS trg_cash_movements_immutable ON tpv.cash_movements;
                DROP TRIGGER IF EXISTS trg_payment_refunds_no_truncate ON tpv.payment_refunds;
                DROP TRIGGER IF EXISTS trg_payment_refunds_immutable ON tpv.payment_refunds;
                DROP TRIGGER IF EXISTS trg_payments_no_truncate ON tpv.payments;
                DROP TRIGGER IF EXISTS trg_payments_immutable ON tpv.payments;
                DROP FUNCTION IF EXISTS tpv.prevent_ledger_change();
                DELETE FROM tpv.role_permissions WHERE permission = 'TakePayments';
                """);
            migrationBuilder.DropTable(
                name: "cash_movements",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "payment_refunds",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "cash_sessions",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "payment_methods",
                schema: "tpv");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_paid_total",
                schema: "tpv",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_total",
                schema: "tpv",
                table: "orders");
        }
    }
}
