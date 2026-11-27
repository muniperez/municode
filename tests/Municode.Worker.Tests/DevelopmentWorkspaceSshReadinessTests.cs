using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet.Common;
using Municode.Worker.DevelopmentWorkspaces;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Fact]
    public async Task EphemeralSshTimeoutThenReadyPreservesPinnedGenerationAndBootstrapsOnlyOnce()
    {
        var (_, vm, operation) = await Seed("allocate", ephemeral: true);
        vm.EnrollmentTokenExpiresAt = clock.GetUtcNow().AddMinutes(30);
        await db.SaveChangesAsync();
        await EnableGitHubCredentials(vm);
        await Hop("server-ready");
        await Hop("ssh-ready");
        var deadline = JsonDocument.Parse((await CurrentOperation(operation.Id)).ProviderEvidenceJson!)
            .RootElement.GetProperty("sshReadinessDeadline").GetDateTimeOffset();
        runtime.SshConnectResults.Enqueue(new SshOperationTimeoutException("Synthetic connect timeout."));
        runtime.SshConnectResults.Enqueue(null);
        runtime.Enroll = async (target, _) =>
        {
            Assert.Equal(vm.Id, target.VmId);
            var fresh = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == target.VmId);
            fresh.EnrolledAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
        };

        await Hop("ssh-ready");

        var pending = await CurrentOperation(operation.Id);
        Assert.Equal("pending", pending.Status);
        Assert.Null(pending.LeaseToken);
        Assert.Equal(deadline, JsonDocument.Parse(pending.ProviderEvidenceJson!)
            .RootElement.GetProperty("sshReadinessDeadline").GetDateTimeOffset());
        Assert.Empty(runtime.Actions);
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.False((await CurrentVm(vm.Id)).GatewayReady);
        Assert.False(await db.DevelopmentWorkspaceGatewayRoutes.AnyAsync());

        clock.Advance(TimeSpan.FromSeconds(10));
        await Hop("bootstrap");
        Assert.Empty(runtime.Actions);
        Assert.Null(runtime.BootstrapConfiguration);
        await Hop("session");
        Assert.NotNull(runtime.BootstrapConfiguration);
        Assert.Equal(vm.BaseCommit, runtime.BootstrapConfiguration.Value
            .GetProperty("repositories")[0].GetProperty("baseSha").GetString());
        Assert.Equal(1, runtime.Actions.Count(x => x.StartsWith("stage:", StringComparison.Ordinal)));
        Assert.True(await Processor().ProcessNextAsync(default));

        var ready = await CurrentVm(vm.Id);
        Assert.Equal("ready", ready.Phase);
        Assert.True(ready.GatewayReady);
        Assert.Equal("completed", (await CurrentOperation(operation.Id)).Status);
        Assert.False(await Processor().ProcessNextAsync(default));
        Assert.Equal(1, runtime.BootstrapCalls);
        Assert.Equal(1, cloud.Creates);
        Assert.Equal(2, runtime.SshReadinessTargets.Count);
        Assert.All(runtime.SshReadinessTargets, target => Assert.Equal(
            new WorkspaceRuntimeTarget(vm.Id, vm.Generation, vm.Kind, "198.51.100.10", vm.SshHostKeyFingerprint!), target));
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("host-key")]
    [InlineData("deadline")]
    [InlineData("generation")]
    public async Task EphemeralSshHardFailureBlocksWithoutBootstrapOrSideEffectRetry(string failure)
    {
        var (_, vm, operation) = await Seed("bootstrap", ephemeral: true);
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await EnableGitHubCredentials(vm);
        await Hop("ssh-ready");
        if (failure == "deadline")
        {
            runtime.SshConnectResults.Enqueue(new SocketException((int)SocketError.ConnectionRefused));
            await Hop("ssh-ready");
            clock.Advance(TimeSpan.FromMinutes(2));
        }
        else if (failure == "generation")
        {
            var fresh = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
            fresh.SshHostKeyGeneration = vm.Generation - 1;
            await db.SaveChangesAsync();
        }
        else
            runtime.SshConnectResults.Enqueue(failure == "authentication"
                ? new SshAuthenticationException("Synthetic denied key.")
                : new SshConnectionException("Synthetic host-key mismatch."));

        await Hop("ssh-ready");

        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.False((await CurrentVm(vm.Id)).GatewayReady);
        Assert.Equal("workspace_reconciliation_required", (await CurrentOperation(operation.Id)).ErrorCode);
        Assert.Equal("failed", (await CurrentOperation(operation.Id)).Status);
        Assert.Empty(runtime.Actions);
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.Empty(runtime.VerifiedRecoverySources);
        Assert.False(await db.DevelopmentWorkspaceGatewayRoutes.AnyAsync());
        Assert.False(await Processor().ProcessNextAsync(default));
        Assert.Equal(failure == "generation" ? 0 : 1, runtime.SshReadinessTargets.Count);
        Assert.Equal(0, cloud.Creates);
    }

    [Theory]
    [InlineData(false, "attach")]
    [InlineData(true, "ssh-ready")]
    public async Task ProviderReadyUsesSshGateOnlyForEphemeral(bool ephemeral, string nextStep)
    {
        await Seed("allocate", ephemeral);
        await Hop("server-ready");
        await Hop(nextStep);
        Assert.Empty(runtime.SshReadinessTargets);
        Assert.Null(runtime.BootstrapConfiguration);
    }
}

public sealed class WorkspaceSshReadinessFailureTests
{
    [Fact]
    public void OnlyTemporaryConnectFailuresAreRetryable()
    {
        Assert.True(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new SshOperationTimeoutException()));
        Assert.True(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new SocketException((int)SocketError.TimedOut)));
        Assert.True(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new SshAuthenticationException()));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(
            new SshConnectionException("Untrusted host.")));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new SocketException((int)SocketError.AccessDenied)));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new IOException()));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new OperationCanceledException()));
        Assert.False(WorkspaceRuntimeTransport.IsTemporarySshConnectFailure(new InvalidOperationException()));
    }
}
