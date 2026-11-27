using Microsoft.EntityFrameworkCore;
using Municode.Common.Data;
using Municode.Common.Services;
using Npgsql;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceDeletionPostgresTests
{
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VerifiedDeletionReleasesProvisionedStorageAndNameWithoutTouchingNeighbors(bool loseResponse) =>
        WithPostgres(fixture => fixture.VerifyPermanentDeletionAsync(loseResponse));

    [PostgresTheory]
    [InlineData("labels", "workspace_delete_volume_mismatch")]
    [InlineData("region", "workspace_delete_volume_mismatch")]
    [InlineData("attachment", "workspace_delete_volume_attached")]
    [InlineData("account", "workspace_delete_identity_mismatch")]
    [InlineData("missing", "workspace_delete_volume_unverified")]
    [InlineData("inventory", "workspace_delete_volume_unexpected")]
    public Task UnverifiedOrMismatchedStorageRemainsReserved(string mismatch, string errorCode) =>
        WithPostgres(fixture => fixture.VerifyUnsafeDeletionAsync(mismatch, errorCode));

    [PostgresFact]
    public Task TerminalCompletionStillRequiresCurrentVerifiedOperationAndLease() =>
        WithPostgres(fixture => fixture.VerifyDeletionFencingAsync());

    [PostgresTheory]
    [InlineData("upsert")]
    [InlineData("read")]
    [InlineData("prune")]
    public Task ResourceHistoryUpsertReadAndPruneWorkOnPostgres(string scenario) =>
        WithPostgres(fixture => scenario switch
            {
                "upsert" => fixture.HeartbeatRecordsNewestSamplePerMinuteIdempotentlyAndPreservesUnknownMetrics(),
                "read" => fixture.HistoryReadIsScopedBoundedOrderedAndLimitedToTheCurrentGeneration(),
                _ => fixture.ExpiredHistoryIsPrunedInBatchesWhileVmsAreStopped()
            });

    private static async Task WithPostgres(Func<DevelopmentWorkspaceProcessorTests, Task> test)
    {
        var fixturePort = int.Parse(Environment.GetEnvironmentVariable("WORKSPACES_TEST_POSTGRES_PORT")!,
            System.Globalization.CultureInfo.InvariantCulture);
        var database = "workspaces_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = fixturePort, Database = database, Username = "postgres", Pooling = false
        };
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>();
        WorkspacesDatabase.ConfigurePostgres(options, connection.ConnectionString);
        var fixture = new DevelopmentWorkspaceProcessorTests(options.Options);
        try
        {
            await fixture.InitializeAsync();
            await test(fixture);
        }
        finally
        {
            await fixture.DisposeAsync();
            await using var db = new WorkspaceDbContext(options.Options);
            await db.Database.EnsureDeletedAsync();
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKSPACES_TEST_POSTGRES_PORT") is null)
            Skip = "Set WORKSPACES_TEST_POSTGRES_PORT to a disposable loopback PostgreSQL fixture.";
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKSPACES_TEST_POSTGRES_PORT") is null)
            Skip = "Set WORKSPACES_TEST_POSTGRES_PORT to a disposable loopback PostgreSQL fixture.";
    }
}

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    internal async Task VerifyUnsafeDeletionAsync(string mismatch, string errorCode)
    {
        var workspace = await SeedRetainedVolume();
        var volume = cloud.Volume!;
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        switch (mismatch)
        {
            case "labels": cloud.Volume = volume with { Labels = new Dictionary<string, string>(volume.Labels) { ["workspace"] = "other" } }; break;
            case "region": cloud.Volume = volume with { Region = "fsn1" }; break;
            case "attachment": cloud.Volume = volume with { ServerId = "other-writer" }; break;
            case "missing": cloud.Volume = null; break;
            case "inventory": cloud.SiblingVolumes.Add("extra", volume with { Id = "extra" }); break;
            case "account":
                var operationId = (await CurrentVm(workspace.PrimaryVmId)).OperationId!;
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE development_workspace_operations SET provider_account_id = 'other' WHERE id = {operationId}");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
        await Hop();
        Assert.Equal(errorCode, (await CurrentVm(workspace.PrimaryVmId)).ErrorCode);
        Assert.Equal("deleting", (await CurrentWorkspace(workspace.Id)).VolumeStatus);
        Assert.Equal(volume.Id, (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Equal(workspace.StorageMonthlyRate, (await CurrentWorkspace(workspace.Id)).StorageMonthlyRate);
        Assert.Empty(cloud.DeletedVolumes);
        Assert.Empty(cloud.DeletedServers);
        Assert.Empty(cloud.DetachedVolumes);
    }

    internal async Task VerifyDeletionFencingAsync()
    {
        var workspace = await SeedRetainedVolume();
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        var claim = (await lifecycle.ClaimOperationAsync("fixture", default))!;
        var unverified = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", unverified.Code);
        var wrongLease = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, "not-current", default));
        Assert.Equal("workspace_operation_stale", wrongLease.Code);
        await lifecycle.UpdateClaimedOperationAsync(claim.Id, claim.LeaseToken!, (operation, _, _) =>
            operation.Step = "delete-verified-volume", default);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET operation_id = NULL WHERE id = {workspace.PrimaryVmId}");
        var staleOperation = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", staleOperation.Code);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET operation_id = {claim.Id} WHERE id = {workspace.PrimaryVmId}");
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET generation = generation + 1 WHERE id = {workspace.PrimaryVmId}");
        var staleGeneration = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", staleGeneration.Code);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE development_workspace_vms SET generation = {claim.Generation} WHERE id = {workspace.PrimaryVmId}");
        clock.Advance(TimeSpan.FromSeconds(policy.OperationLeaseSeconds + 1));
        var expiredLease = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
            lifecycle.CompleteDeletionAsync(claim.Id, claim.LeaseToken!, default));
        Assert.Equal("workspace_operation_stale", expiredLease.Code);
        Assert.Equal("volume-retained", (await CurrentWorkspace(workspace.Id)).ProviderVolumeId);
        Assert.Empty(cloud.DeletedVolumes);
    }
}
