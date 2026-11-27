using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBudgetNotificationsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing budgets keep the in-app alerts they had before channels became configurable.
            migrationBuilder.AddColumn<bool>(
                name: "in_app_alerts_enabled",
                table: "budgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "webhook_enabled",
                table: "budgets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "webhook_secret_path",
                table: "budgets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_secret_provider",
                table: "budgets",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_secret_version",
                table: "budgets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_url",
                table: "budgets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "budget_notifications",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    budget_id = table.Column<string>(type: "TEXT", nullable: false),
                    budget_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    channel = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_budget_notifications_budgets_budget_id",
                        column: x => x.budget_id,
                        principalTable: "budgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_notifications_budget_id_budget_revision_period_threshold_channel",
                table: "budget_notifications",
                columns: new[] { "budget_id", "budget_revision", "period", "threshold", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budget_notifications_status_channel",
                table: "budget_notifications",
                columns: new[] { "status", "channel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_notifications");

            migrationBuilder.DropColumn(
                name: "in_app_alerts_enabled",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "webhook_enabled",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "webhook_secret_path",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "webhook_secret_provider",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "webhook_secret_version",
                table: "budgets");

            migrationBuilder.DropColumn(
                name: "webhook_url",
                table: "budgets");
        }
    }
}
