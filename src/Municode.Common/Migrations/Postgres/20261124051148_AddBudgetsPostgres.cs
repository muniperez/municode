using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBudgetsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    monthly_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    thresholds_json = table.Column<string>(type: "text", nullable: false),
                    provider_account_ids_json = table.Column<string>(type: "text", nullable: false),
                    alert_period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    alert_threshold_percent = table.Column<int>(type: "integer", nullable: true),
                    alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
