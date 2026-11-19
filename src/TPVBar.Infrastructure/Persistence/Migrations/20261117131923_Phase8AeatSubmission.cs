using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPVBar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8AeatSubmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "recipient_name",
                schema: "tpv",
                table: "invoices",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(150)",
                oldMaxLength: 150,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "issuer_name",
                schema: "tpv",
                table: "invoices",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(150)",
                oldMaxLength: 150);

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "tpv",
                table: "billing_submissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "csv",
                schema: "tpv",
                table: "billing_submissions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "error_code",
                schema: "tpv",
                table: "billing_submissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_attempt_at",
                schema: "tpv",
                table: "billing_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "tpv",
                table: "billing_submissions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "tpv",
                table: "billing_submissions");

            migrationBuilder.DropColumn(
                name: "csv",
                schema: "tpv",
                table: "billing_submissions");

            migrationBuilder.DropColumn(
                name: "error_code",
                schema: "tpv",
                table: "billing_submissions");

            migrationBuilder.DropColumn(
                name: "last_attempt_at",
                schema: "tpv",
                table: "billing_submissions");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "tpv",
                table: "billing_submissions");

            migrationBuilder.AlterColumn<string>(
                name: "recipient_name",
                schema: "tpv",
                table: "invoices",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "issuer_name",
                schema: "tpv",
                table: "invoices",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);
        }
    }
}
