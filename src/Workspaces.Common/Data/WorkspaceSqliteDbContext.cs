using Microsoft.EntityFrameworkCore;

namespace Workspaces.Common.Data;

// EF tracks each database engine's migrations and model snapshot independently.
public sealed class WorkspaceSqliteDbContext(DbContextOptions<WorkspaceSqliteDbContext> options)
    : WorkspaceDbContext(options);
