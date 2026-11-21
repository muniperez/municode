using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Workspaces.Common.Data;

namespace Workspaces.Api.Services;

public sealed class PostgresSchemaFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>();
        WorkspacesDatabase.ConfigurePostgres(options, "Host=localhost;Database=workspaces");
        return new WorkspaceDbContext(options.Options);
    }
}

public sealed class SqliteSchemaFactory : IDesignTimeDbContextFactory<WorkspaceSqliteDbContext>
{
    public WorkspaceSqliteDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkspaceSqliteDbContext>();
        WorkspacesDatabase.ConfigureSqlite(options, "Data Source=:memory:");
        return new WorkspaceSqliteDbContext(options.Options);
    }
}
