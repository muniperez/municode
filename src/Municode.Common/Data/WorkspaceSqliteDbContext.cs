using Microsoft.EntityFrameworkCore;

namespace Municode.Common.Data;

// EF tracks each database engine's migrations and model snapshot independently.
public sealed class WorkspaceSqliteDbContext(DbContextOptions<WorkspaceSqliteDbContext> options)
    : WorkspaceDbContext(options);
