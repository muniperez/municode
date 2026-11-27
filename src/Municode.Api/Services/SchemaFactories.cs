using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Municode.Common.Data;

namespace Municode.Api.Services;

public sealed class PostgresSchemaFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>();
        MunicodeDatabase.ConfigurePostgres(options, "Host=localhost;Database=municode");
        return new WorkspaceDbContext(options.Options);
    }
}

public sealed class SqliteSchemaFactory : IDesignTimeDbContextFactory<WorkspaceSqliteDbContext>
{
    public WorkspaceSqliteDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspaceSqliteDbContext>();
        MunicodeDatabase.ConfigureSqlite(options, "Data Source=:memory:");
        return new WorkspaceSqliteDbContext(options.Options);
    }
}
