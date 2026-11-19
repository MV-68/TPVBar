using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7Invoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_chain_head",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_chain_head", x => x.id);
                    table.CheckConstraint("ck_billing_chain_head_single", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    series = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    full_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issuer_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    issuer_nif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    issuer_address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    recipient_nif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    recipient_address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    rectified_invoice_id = table.Column<long>(type: "bigint", nullable: true),
                    rectification_reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    @base = table.Column<decimal>(name: "base", type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.CheckConstraint("ck_invoices_number", "number > 0");
                    table.CheckConstraint("ck_invoices_recipient", "kind <> 'Complete' OR (recipient_name IS NOT NULL AND recipient_nif IS NOT NULL AND recipient_address IS NOT NULL)");
                    table.CheckConstraint("ck_invoices_rectified", "(kind = 'Rectificative') = (rectified_invoice_id IS NOT NULL)");
                    table.CheckConstraint("ck_invoices_sign", "(kind = 'Rectificative' AND total < 0) OR (kind <> 'Rectificative' AND total > 0)");
                    table.CheckConstraint("ck_invoices_totals", "base + tax = total");
                    table.ForeignKey(
                        name: "fk_invoices_invoices_rectified_invoice_id",
                        column: x => x.rectified_invoice_id,
                        principalSchema: "tpv",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "tpv",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_records",
                schema: "tpv",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    invoice_id = table.Column<long>(type: "bigint", nullable: false),
                    issuer_nif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    issue_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    generated_at_text = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_records", x => x.sequence);
                    table.CheckConstraint("ck_billing_records_hash", "char_length(hash) = 64");
                    table.CheckConstraint("ck_billing_records_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "fk_billing_records_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tpv",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    invoice_id = table.Column<long>(type: "bigint", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    @base = table.Column<decimal>(name: "base", type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    order_line_id = table.Column<long>(type: "bigint", nullable: true),
                    rectifies_line_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.CheckConstraint("ck_invoice_lines_totals", "base + tax = total");
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "tpv",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_submissions",
                schema: "tpv",
                columns: table => new
                {
                    record_sequence = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_submissions", x => x.record_sequence);
                    table.ForeignKey(
                        name: "fk_billing_submissions_billing_records_record_sequence",
                        column: x => x.record_sequence,
                        principalSchema: "tpv",
                        principalTable: "billing_records",
                        principalColumn: "sequence",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_billing_records_hash",
                schema: "tpv",
                table: "billing_records",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_billing_records_invoice_type",
                schema: "tpv",
                table: "billing_records",
                columns: new[] { "invoice_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_billing_records_previous_hash",
                schema: "tpv",
                table: "billing_records",
                column: "previous_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_submissions_status",
                schema: "tpv",
                table: "billing_submissions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice",
                schema: "tpv",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_rectifies",
                schema: "tpv",
                table: "invoice_lines",
                column: "rectifies_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_issue_date",
                schema: "tpv",
                table: "invoices",
                column: "issue_date");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_order",
                schema: "tpv",
                table: "invoices",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_rectified",
                schema: "tpv",
                table: "invoices",
                column: "rectified_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_full_number",
                schema: "tpv",
                table: "invoices",
                column: "full_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoices_series_number",
                schema: "tpv",
                table: "invoices",
                columns: new[] { "series", "number" },
                unique: true);

            // Facturas, líneas y registros de facturación son inmutables (misma función que los registros de dinero):
            // los errores se corrigen con rectificativas o anulaciones, nunca editando ni borrando.
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_invoices_immutable BEFORE UPDATE OR DELETE ON tpv.invoices
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_invoices_no_truncate BEFORE TRUNCATE ON tpv.invoices
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_invoice_lines_immutable BEFORE UPDATE OR DELETE ON tpv.invoice_lines
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_invoice_lines_no_truncate BEFORE TRUNCATE ON tpv.invoice_lines
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_billing_records_immutable BEFORE UPDATE OR DELETE ON tpv.billing_records
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_ledger_change();
                CREATE TRIGGER trg_billing_records_no_truncate BEFORE TRUNCATE ON tpv.billing_records
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_ledger_change();

                -- La cabeza de la cadena se actualiza en cada emisión, pero no se puede borrar ni vaciar.
                CREATE FUNCTION tpv.prevent_chain_head_removal() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'La cabeza de la cadena de facturación no se puede borrar.' USING ERRCODE = 'restrict_violation';
                END;
                $$;
                CREATE TRIGGER trg_billing_chain_head_no_delete BEFORE DELETE ON tpv.billing_chain_head
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_chain_head_removal();
                CREATE TRIGGER trg_billing_chain_head_no_truncate BEFORE TRUNCATE ON tpv.billing_chain_head
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_chain_head_removal();

                INSERT INTO tpv.billing_chain_head (id, last_sequence, last_hash) VALUES (1, 0, '');
                """);

            // Permisos nuevos para roles que ya existían (el seed solo asigna permisos al crear un rol).
            migrationBuilder.Sql("""
                INSERT INTO tpv.role_permissions (role_id, permission)
                SELECT r.id, v.permission
                FROM tpv.roles r
                JOIN (VALUES
                    ('Administrador', 'IssueInvoices'), ('Administrador', 'RectifyInvoices'),
                    ('Encargado', 'IssueInvoices'), ('Encargado', 'RectifyInvoices'),
                    ('Caja', 'IssueInvoices')
                ) AS v(role_name, permission) ON v.role_name = r.name
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM tpv.role_permissions WHERE permission IN ('IssueInvoices', 'RectifyInvoices');
                DROP TRIGGER IF EXISTS trg_billing_chain_head_no_truncate ON tpv.billing_chain_head;
                DROP TRIGGER IF EXISTS trg_billing_chain_head_no_delete ON tpv.billing_chain_head;
                DROP FUNCTION IF EXISTS tpv.prevent_chain_head_removal();
                DROP TRIGGER IF EXISTS trg_billing_records_no_truncate ON tpv.billing_records;
                DROP TRIGGER IF EXISTS trg_billing_records_immutable ON tpv.billing_records;
                DROP TRIGGER IF EXISTS trg_invoice_lines_no_truncate ON tpv.invoice_lines;
                DROP TRIGGER IF EXISTS trg_invoice_lines_immutable ON tpv.invoice_lines;
                DROP TRIGGER IF EXISTS trg_invoices_no_truncate ON tpv.invoices;
                DROP TRIGGER IF EXISTS trg_invoices_immutable ON tpv.invoices;
                """);

            migrationBuilder.DropTable(
                name: "billing_chain_head",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "billing_submissions",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "billing_records",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "tpv");
        }
    }
}
