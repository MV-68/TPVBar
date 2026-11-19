using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tpv");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workstation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "tpv",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    normalized_user_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_access_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_failed_access_count", "failed_access_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "tpv",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    permission = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission });
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "tpv",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "tpv",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "tpv",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "tpv",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_date",
                schema: "tpv",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity",
                schema: "tpv",
                table: "audit_events",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ux_roles_name",
                schema: "tpv",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "tpv",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_normalized_user_name",
                schema: "tpv",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            // La auditoría es inmutable: la base de datos rechaza modificar o borrar eventos.
            migrationBuilder.Sql("""
                CREATE FUNCTION tpv.prevent_audit_events_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'tpv.audit_events es inmutable: no se permite % ', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_audit_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON tpv.audit_events
                    FOR EACH ROW EXECUTE FUNCTION tpv.prevent_audit_events_change();

                CREATE TRIGGER trg_audit_events_no_truncate
                    BEFORE TRUNCATE ON tpv.audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION tpv.prevent_audit_events_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_events_no_truncate ON tpv.audit_events;
                DROP TRIGGER IF EXISTS trg_audit_events_no_update_delete ON tpv.audit_events;
                DROP FUNCTION IF EXISTS tpv.prevent_audit_events_change();
                """);

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "tpv");

            migrationBuilder.DropTable(
                name: "users",
                schema: "tpv");
        }
    }
}
