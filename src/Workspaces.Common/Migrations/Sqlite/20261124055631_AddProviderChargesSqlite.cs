using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workspaces.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddProviderChargesSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_charges",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    amount = table.Column<decimal>(type: "TEXT", nullable: true),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    source = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    as_of = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    attempted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_charges_provider_accounts_organization_id_provider_account_id",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_charges_organization_id_provider_account_id_period",
                table: "provider_charges",
                columns: new[] { "organization_id", "provider_account_id", "period" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_charges");
        }
    }
}
