using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3KitchenAndPrinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "tpv",
                table: "kitchen_tickets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                schema: "tpv",
                table: "kitchen_tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "print_agent_heartbeats",
                schema: "tpv",
                columns: table => new
                {
                    agent_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_agent_heartbeats", x => x.agent_name);
                });

            migrationBuilder.CreateTable(
                name: "printers",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    connection = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    paper_width_mm = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printers", x => x.id);
                    table.CheckConstraint("ck_printers_paper_width", "paper_width_mm IN (58, 80)");
                });

            migrationBuilder.CreateTable(
                name: "print_jobs",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    printer_id = table.Column<int>(type: "integer", nullable: false),
                    job_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    delivery_level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_jobs", x => x.id);
                    table.CheckConstraint("ck_print_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_print_jobs_max_attempts", "max_attempts > 0");
                    table.ForeignKey(
                        name: "fk_print_jobs_printers_printer_id",
                        column: x => x.printer_id,
                        principalSchema: "tpv",
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "printer_routes",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    printer_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_printer_routes", x => x.id);
                    table.ForeignKey(
                        name: "fk_printer_routes_printers_printer_id",
                        column: x => x.printer_id,
                        principalSchema: "tpv",
                        principalTable: "printers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "print_job_attempts",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<long>(type: "bigint", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    agent = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    delivery_level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_job_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_print_job_attempts_print_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "tpv",
                        principalTable: "print_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_attempts_job",
                schema: "tpv",
                table: "print_job_attempts",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_pending",
                schema: "tpv",
                table: "print_jobs",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_print_jobs_printer_id",
                schema: "tpv",
                table: "print_jobs",
                column: "printer_id");

            migrationBuilder.CreateIndex(
                name: "ux_print_jobs_dedupe",
                schema: "tpv",
                table: "print_jobs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_printer_routes_printer_id",
                schema: "tpv",
                table: "printer_routes",
                column: "printer_id");

            migrationBuilder.CreateIndex(
                name: "ux_printer_routes_station",
                schema: "tpv",
                table: "printer_routes",
                column: "station",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_printers_name",
                schema: "tpv",
                table: "printers",
                column: "name",
                unique: true);

            // Tickets anteriores a esta migración: su última actualización es la de envío.
            migrationBuilder.Sql("UPDATE tpv.kitchen_tickets SET updated_at = sent_at;");

            // Permisos nuevos para roles que ya existían (el seed solo asigna permisos al crear un rol).
            migrationBuilder.Sql("""
                INSERT INTO tpv.role_permissions (role_id, permission)
                SELECT r.id, v.permission
                FROM tpv.roles r
                JOIN (VALUES
                    ('Administrador', 'UseKitchen'), ('Encargado', 'UseKitchen'), ('Cocina', 'UseKitchen'),
                    ('Administrador', 'ManagePrinting'), ('Encargado', 'ManagePrinting')
                ) AS v(role_name, permission) ON v.role_name = r.name
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM tpv.role_permissions WHERE permission IN ('UseKitchen', 'ManagePrinting');");

            migrationBuilder.DropTable(
                name: "print_agent_heartbeats",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "print_job_attempts",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "printer_routes",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "print_jobs",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "printers",
                schema: "tpv");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "tpv",
                table: "kitchen_tickets");

            migrationBuilder.DropColumn(
                name: "updated_by",
                schema: "tpv",
                table: "kitchen_tickets");
        }
    }
}
