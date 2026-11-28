using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddWorkspaceCheckpointStatusPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "checkpoint_status",
                table: "development_workspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_checkpoint_at",
                table: "development_workspaces",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "checkpoint_status",
                table: "development_workspaces");

            migrationBuilder.DropColumn(
                name: "last_checkpoint_at",
                table: "development_workspaces");
        }
    }
}
