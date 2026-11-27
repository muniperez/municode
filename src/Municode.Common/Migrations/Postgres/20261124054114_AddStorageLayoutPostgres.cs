using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddStorageLayoutPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "storage_layout",
                table: "development_workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "storage_layout",
                table: "development_workspaces");
        }
    }
}
