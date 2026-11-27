using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Municode.Common.Data;

namespace Municode.Api.Services;

internal static class MigrationInspectionCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args is not [("list" or "status"), "--json"])
        {
            await error.WriteLineAsync("Usage: Municode.Api migrations list|status --json");
            return 2;
        }

        if (args[0] == "list")
        {
            await using var postgres = new PostgresSchemaFactory().CreateDbContext([]);
            await using var sqlite = new SqliteSchemaFactory().CreateDbContext([]);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                postgres = postgres.Database.GetMigrations(),
                sqlite = sqlite.Database.GetMigrations()
            }, JsonSerializerOptions.Web));
            return 0;
        }

        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddMunicodeDatabase(readOnlySqlite: true);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        await output.WriteLineAsync(JsonSerializer.Serialize(await ReadStatusAsync(db), JsonSerializerOptions.Web));
        return 0;
    }

    internal static async Task<MigrationStatus> ReadStatusAsync(WorkspaceDbContext db, CancellationToken ct = default)
    {
        var known = db.Database.GetMigrations().ToArray();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = db.Database.IsSqlite()
                ? $"SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = '{MunicodeDatabase.MigrationsHistoryTable}')"
                : $"""
                    SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c
                        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'public' AND c.relname = '{MunicodeDatabase.MigrationsHistoryTable}'
                            AND c.relkind IN ('r', 'p'))
                    """;
            var historyTableExists = Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
            var applied = historyTableExists
                ? (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray()
                : [];
            return new(historyTableExists,
                known.Intersect(applied, StringComparer.Ordinal).ToArray(),
                known.Except(applied, StringComparer.Ordinal).ToArray(),
                applied.Except(known, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    internal sealed record MigrationStatus(bool HistoryTableExists, string[] Applied, string[] Pending, string[] UnknownApplied);
}
