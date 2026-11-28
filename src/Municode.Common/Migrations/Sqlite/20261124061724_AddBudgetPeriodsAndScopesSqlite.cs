using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBudgetPeriodsAndScopesSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "monthly_amount",
                table: "budgets",
                newName: "amount");

            migrationBuilder.AddColumn<string>(
                name: "period",
                table: "budgets",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "month");

            migrationBuilder.AddColumn<string>(
                name: "vm_ids_json",
                table: "budgets",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "workspace_ids_json",
                table: "budgets",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
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
        }
    }
}
