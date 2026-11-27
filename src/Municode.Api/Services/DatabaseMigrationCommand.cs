using Microsoft.EntityFrameworkCore;
using Municode.Common.Data;

namespace Municode.Api.Services;

internal static class DatabaseMigrationCommand
{
    private const long PostgresMigrationLock = 5577006791947779421;

    public static async Task RunAsync(WorkspaceDbContext db, CancellationToken ct = default)
    {
        if (db.Database.IsSqlite())
            await SqliteDatabaseConfigurator.ConfigureDatabaseAsync(db);

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({PostgresMigrationLock})", ct);

            try
            {
                await RejectUnrecognizedStateAsync(db, ct);
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({PostgresMigrationLock})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task RejectUnrecognizedStateAsync(WorkspaceDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Any(id => !known.Contains(id)))
            throw new InvalidOperationException("This release cannot use a newer Municode database.");

        if (applied.Length > 0) return;

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = db.Database.IsSqlite()
            ? $"""
                SELECT EXISTS (SELECT 1 FROM sqlite_schema
                    WHERE type IN ('table', 'view') AND name NOT IN ('sqlite_sequence', '{MunicodeDatabase.MigrationsHistoryTable}', '__EFMigrationsLock'))
                """
            : $"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                        AND c.relname <> '{MunicodeDatabase.MigrationsHistoryTable}')
                """;
        if (Convert.ToBoolean(await command.ExecuteScalarAsync(ct)))
            throw new InvalidOperationException("Municode requires a dedicated, empty database for its first migration.");
    }
}
