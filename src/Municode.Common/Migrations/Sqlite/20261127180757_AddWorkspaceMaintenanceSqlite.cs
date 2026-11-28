using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddWorkspaceMaintenanceSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_maintenance",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_maintenance", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "workspace_maintenance",
                columns: new[] { "id", "enabled" },
                values: new object[] { 1, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_maintenance");
        }
    }
}
