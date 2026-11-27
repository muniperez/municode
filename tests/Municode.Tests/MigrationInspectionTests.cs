using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Municode.Api.Services;
using Municode.Common.Data;

namespace Municode.Tests;

public sealed class MigrationInspectionTests
{
    [Fact]
    public async Task ListReportsBothProvidersWithoutOpeningADatabase()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await MigrationInspectionCommand.RunAsync(["list", "--json"], output, error));
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        await using var postgres = new PostgresSchemaFactory().CreateDbContext([]);
        await using var sqlite = new SqliteSchemaFactory().CreateDbContext([]);
        Assert.Equal(postgres.Database.GetMigrations(), json.RootElement.GetProperty("postgres").Deserialize<string[]>());
        Assert.Equal(sqlite.Database.GetMigrations(), json.RootElement.GetProperty("sqlite").Deserialize<string[]>());
    }

    [Fact]
    public async Task StatusDistinguishesMissingAndEmptyHistoryAndSeparatesUnknownAppliedMigrations()
    {
        await using var db = new SqliteSchemaFactory().CreateDbContext([]);
        await db.Database.OpenConnectionAsync();
        var known = db.Database.GetMigrations().ToArray();
        var missing = await MigrationInspectionCommand.ReadStatusAsync(db);
        Assert.False(missing.HistoryTableExists);
        Assert.Empty(missing.Applied);
        Assert.Equal(known, missing.Pending);
        Assert.Empty(missing.UnknownApplied);

        var history = db.GetService<IHistoryRepository>();
        await history.CreateAsync();
        var empty = await MigrationInspectionCommand.ReadStatusAsync(db);
        Assert.True(empty.HistoryTableExists);
        Assert.Empty(empty.Applied);
        Assert.Equal(known, empty.Pending);
        Assert.Empty(empty.UnknownApplied);

        await DatabaseMigrationCommand.RunAsync(db);
        const string unknownId = "20990101000000_FutureMigration";
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(unknownId, "10.0.0")));

        var migrated = await MigrationInspectionCommand.ReadStatusAsync(db);
        Assert.True(migrated.HistoryTableExists);
        Assert.Equal(known, migrated.Applied);
        Assert.Empty(migrated.Pending);
        Assert.Equal([unknownId], migrated.UnknownApplied);
    }
}
