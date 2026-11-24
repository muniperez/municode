using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workspaces.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBudgetsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    monthly_amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    thresholds_json = table.Column<string>(type: "TEXT", nullable: false),
                    provider_account_ids_json = table.Column<string>(type: "TEXT", nullable: false),
                    alert_period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    alert_threshold_percent = table.Column<int>(type: "INTEGER", nullable: true),
                    alerted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.id);
                    table.ForeignKey(
                        name: "FK_budgets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budgets_organization_id_name",
                table: "budgets",
                columns: new[] { "organization_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budgets");
        }
    }
}
