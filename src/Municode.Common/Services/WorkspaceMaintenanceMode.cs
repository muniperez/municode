using Microsoft.EntityFrameworkCore;
using Municode.Common.Data;
using Municode.Common.Models;

namespace Municode.Common.Services;

public sealed record WorkspaceMaintenanceStatus(bool Enabled, int NonterminalOperations);

public static class WorkspaceMaintenanceMode
{
    public const string ErrorCode = "workspace_maintenance";
    public const string RetryAfterSeconds = "60";

    private static IQueryable<WorkspaceMaintenance> Flag(WorkspaceDbContext db) =>
        db.WorkspaceMaintenanceFlags.Where(x => x.Id == WorkspaceMaintenance.SingletonId);

    // Both the switch and admission use this lock through commit. SQLite's default
    // non-deferred write transaction already serializes them across processes.
    internal static async Task LockAdmissionAsync(WorkspaceDbContext db, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({DevelopmentWorkspaceService.AdmissionLockId})", ct);
    }

    public static async Task SetAsync(WorkspaceDbContext db, bool enabled, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdmissionAsync(db, ct);
        if (await Flag(db).ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, enabled), ct) != 1)
            throw new InvalidOperationException("The workspace maintenance flag is missing. Check the database migrations.");
        await tx.CommitAsync(ct);
    }

    public static Task<WorkspaceMaintenanceStatus> GetStatusAsync(WorkspaceDbContext db, CancellationToken ct = default) =>
        Flag(db).Select(x => new WorkspaceMaintenanceStatus(x.Enabled, db.DevelopmentWorkspaceOperations.Count(op =>
            op.Status != "completed" && op.Status != "failed" && op.Status != "cancelled" && op.Status != "superseded")))
            .SingleAsync(ct);

    internal static async Task RequireAdmissionAsync(WorkspaceDbContext db, CancellationToken ct)
    {
        if (await Flag(db).Select(x => x.Enabled).SingleAsync(ct))
            throw new DevelopmentWorkspaceException(ErrorCode, "Workspace operations are paused for maintenance. Try again later.");
    }
}
