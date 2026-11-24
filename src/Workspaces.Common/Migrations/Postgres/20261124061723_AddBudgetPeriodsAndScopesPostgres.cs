using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workspaces.Common.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBudgetPeriodsAndScopesPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "monthly_amount",
                table: "budgets",
                newName: "amount");

            migrationBuilder.AlterColumn<string>(
                name: "alert_period",
                table: "budgets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "period",
                table: "budgets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "month");

            migrationBuilder.AddColumn<string>(
                name: "vm_ids_json",
                table: "budgets",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "workspace_ids_json",
                table: "budgets",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AlterColumn<string>(
                name: "period",
                table: "budget_notifications",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "period",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "vm_ids_json",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "workspace_ids_json",
                table: "budgets");

            migrationBuilder.RenameColumn(
                name: "amount",
                table: "budgets",
                newName: "monthly_amount");

            migrationBuilder.AlterColumn<string>(
                name: "alert_period",
                table: "budgets",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "period",
                table: "budget_notifications",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }
    }
}
