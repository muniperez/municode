using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddStorageLayoutSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "storage_layout",
                table: "development_workspaces",
                type: "INTEGER",
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
