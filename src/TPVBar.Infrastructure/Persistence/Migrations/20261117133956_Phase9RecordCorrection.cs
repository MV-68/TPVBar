using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase9RecordCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_billing_records_invoice_type",
                schema: "tpv",
                table: "billing_records");

            migrationBuilder.AddColumn<long>(
                name: "corrects_sequence",
                schema: "tpv",
                table: "billing_records",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "rejected_before",
                schema: "tpv",
                table: "billing_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_billing_records_invoice_type",
                schema: "tpv",
                table: "billing_records",
                columns: new[] { "invoice_id", "type" },
                unique: true,
                filter: "corrects_sequence IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_billing_records_corrects",
                schema: "tpv",
                table: "billing_records",
                sql: "corrects_sequence IS NULL OR corrects_sequence < sequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_billing_records_invoice_type",
                schema: "tpv",
                table: "billing_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_billing_records_corrects",
                schema: "tpv",
                table: "billing_records");

            migrationBuilder.DropColumn(
                name: "corrects_sequence",
                schema: "tpv",
                table: "billing_records");

            migrationBuilder.DropColumn(
                name: "rejected_before",
                schema: "tpv",
                table: "billing_records");

            migrationBuilder.CreateIndex(
                name: "ux_billing_records_invoice_type",
                schema: "tpv",
                table: "billing_records",
                columns: new[] { "invoice_id", "type" },
                unique: true);
        }
    }
}
