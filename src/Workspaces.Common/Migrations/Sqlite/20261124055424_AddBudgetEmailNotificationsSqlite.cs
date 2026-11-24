using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workspaces.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBudgetEmailNotificationsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_budget_notifications_budget_id_budget_revision_period_threshold_channel",
                table: "budget_notifications");

            migrationBuilder.AddColumn<string>(
                name: "email_audience",
                table: "budgets",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "admins");

            migrationBuilder.AddColumn<bool>(
                name: "email_enabled",
                table: "budgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "recipient",
                table: "budget_notifications",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_budget_notifications_budget_id_budget_revision_period_threshold_channel_recipient",
                table: "budget_notifications",
                columns: new[] { "budget_id", "budget_revision", "period", "threshold", "channel", "recipient" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_budget_notifications_budget_id_budget_revision_period_threshold_channel_recipient",
                table: "budget_notifications");

            migrationBuilder.DropColumn(
                name: "email_audience",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "email_enabled",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "recipient",
                table: "budget_notifications");

            migrationBuilder.CreateIndex(
                name: "IX_budget_notifications_budget_id_budget_revision_period_threshold_channel",
                table: "budget_notifications",
                columns: new[] { "budget_id", "budget_revision", "period", "threshold", "channel" },
                unique: true);
        }
    }
}
