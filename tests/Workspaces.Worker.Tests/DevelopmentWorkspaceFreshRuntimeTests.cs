using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workspaces.Api.Controllers;
using Workspaces.Common.Models;
using Workspaces.Worker.DevelopmentWorkspaces;

namespace Workspaces.Worker.Tests.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPrimarySurvivesStopAndResumeWithSameVolumeAndNewGeneration(bool loseEnrollmentReply)
    {
        cloud.AllowProvisioning = true;
        runtime.VolumeFilesystemUuid = "filesystem-retained";
        UseEnrollment(loseEnrollmentReply);

        var workspace = await lifecycle.CreateAsync(new("org", "Fresh primary", "image", "standard", "plan", 20, "fresh"), "actor", default);
        var vmId = workspace.PrimaryVmId;
        await lifecycle.StartAsync(workspace.Id, "org", default);
        var first = await ProcessUntilAsync(vmId, "ready");
        var retained = await CurrentWorkspace(workspace.Id);
        Assert.Equal("mounted", retained.VolumeStatus);
        Assert.True(first.GatewayReady);
        Assert.Equal(1, first.Generation);
        Assert.NotNull(first.EnrolledAt);

        await lifecycle.StopVmAsync(workspace.Id, "org", vmId, first.Generation, default);
        var stopped = await ProcessUntilAsync(vmId, "stopped");
        Assert.False(stopped.ComputeReserved);
        Assert.Equal("retained", (await CurrentWorkspace(workspace.Id)).VolumeStatus);
        Assert.Equal(retained.ProviderVolumeId, cloud.Volume?.Id);
        Assert.Null(cloud.Volume?.ServerId);

        await lifecycle.StartVmAsync(workspace.Id, "org", vmId, stopped.Generation, default);
        var second = await ProcessUntilAsync(vmId, "ready");
        var resumed = await CurrentWorkspace(workspace.Id);
        Assert.Equal(2, second.Generation);
        Assert.Equal(retained.ProviderVolumeId, resumed.ProviderVolumeId);
        Assert.Equal(retained.VolumeFilesystemUuid, resumed.VolumeFilesystemUuid);
        Assert.Equal("mounted", resumed.VolumeStatus);
        Assert.True(second.GatewayReady);
        Assert.Equal(1, cloud.VolumeCreates);
        Assert.Equal(2, cloud.Attaches);
        Assert.Null(second.BaseVerifiedGeneration);
        Assert.NotEqual(first.SshHostKeyFingerprint, second.SshHostKeyFingerprint);

        await lifecycle.StopVmAsync(workspace.Id, "org", vmId, second.Generation, default);
        await ProcessUntilAsync(vmId, "stopped");
        await lifecycle.DeleteAsync(workspace.Id, "org", true, "actor", default);
        await ProcessUntilAsync(vmId, "deleted");
        Assert.Equal(retained.ProviderVolumeId, Assert.Single(cloud.DeletedVolumes));
        Assert.Null(cloud.Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageLayoutIsChosenOnlyWhenTheVolumeIsCreated(bool createdLocal)
    {
        cloud.AllowProvisioning = true;
        runtime.VolumeFilesystemUuid = "filesystem-retained";
        workerOptions.LocalCheckouts = createdLocal;
        UseEnrollment();
        var workspace = await lifecycle.CreateAsync(new("org", "Layout fixture", "image", "standard", "plan", 20, "layout"), "actor", default);
        var vmId = workspace.PrimaryVmId;
        await lifecycle.StartAsync(workspace.Id, "org", default);
        var first = await ProcessUntilAsync(vmId, "ready");
        var expected = createdLocal ? 2 : 1;
        Assert.Equal(expected, (await CurrentWorkspace(workspace.Id)).StorageLayout);
        Assert.Equal(createdLocal, runtime.BootstrapConfiguration!.Value.TryGetProperty("storageLayout", out _));
        var retained = await CurrentWorkspace(workspace.Id);
        if (createdLocal)
            runtime.Checkpoint = (target, operationId) =>
                FinalCheckpoint(retained, target, operationId, target.Generation, retained.ProviderVolumeId!);

        await lifecycle.StopVmAsync(workspace.Id, "org", vmId, first.Generation, default);
        var stopped = await ProcessUntilAsync(vmId, "stopped");
        Assert.Equal(createdLocal, stopped.QuiescenceReceipt!.Contains("\"checkpoint\""));

        // Flipping the option never converts a retained volume.
        workerOptions.LocalCheckouts = !createdLocal;
        await lifecycle.StartVmAsync(workspace.Id, "org", vmId, stopped.Generation, default);
        await ProcessUntilAsync(vmId, "ready");
        Assert.Equal(expected, (await CurrentWorkspace(workspace.Id)).StorageLayout);
        Assert.Equal(createdLocal, runtime.BootstrapConfiguration!.Value.TryGetProperty("storageLayout", out var layout) &&
            layout.GetInt32() == 2);
    }

    [Fact]
    public async Task RestartFencesPersistedPortsAndReadyMonitorRestoresCurrentGeneration()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned";
        vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await Hop();
        Assert.True(await DevelopmentWorkspaceWorker.InvalidateRelayPortsAsync(db, default));
        Assert.True(await DevelopmentWorkspaceWorker.InvalidateRelayPortsAsync(db, default));
        var routes = db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().Where(x => x.VmId == vm.Id);
        Assert.All(await routes.ToListAsync(), route => Assert.Equal(0, route.LoopbackPort));

        await Processor().MonitorReadyAsync(default);
        var t3 = await routes.SingleAsync(x => x.Service == "t3");
        Assert.Equal(tunnels.Port, t3.LoopbackPort);
        Assert.Equal(vm.Generation, t3.Generation);
        Assert.Null(t3.RevokedAt);
    }

    [Fact]
    public async Task FreshEphemeralSavesAndRecoversTheVerifiedRemoteCommitWithoutStorage()
    {
        cloud.AllowProvisioning = true;
        UseEnrollment();
        var workspace = await lifecycle.CreateAsync(new("org", "Git fixture", "image", "standard", "plan", 20, "git"), "actor", default);
        workspace = await lifecycle.LaunchEphemeralAsync(workspace.Id, "org", new("image", "standard", "plan",
            "https://github.com/fixture-owner/project.git", "refs/heads/main", new('b', 40), true, true, "ephemeral"),
            "actor", default);
        var vmId = workspace.Vms.Single(x => x.Kind == DevelopmentWorkspaceVmKinds.Ephemeral).Id;
        var ready = await ProcessUntilAsync(vmId, "ready");
        Assert.Equal(ready.Generation, ready.BaseVerifiedGeneration);
        Assert.Equal(new string('b', 40), ready.BaseCommit);

        await lifecycle.StopVmAsync(workspace.Id, "org", vmId, ready.Generation, default);
        var saved = await ProcessUntilAsync(vmId, "stopped");
        Assert.Equal(new string('c', 40), saved.VerifiedRemoteCommit);
        Assert.Equal($"refs/heads/workspaces/{vmId}/g1", saved.VerifiedRemoteRef);

        await lifecycle.StartVmAsync(workspace.Id, "org", vmId, saved.Generation, default);
        var recovered = await ProcessUntilAsync(vmId, "ready");
        Assert.Equal(2, recovered.Generation);
        Assert.Equal(2, recovered.BaseVerifiedGeneration);
        Assert.Equal(saved.VerifiedRemoteCommit, recovered.BaseCommit);
        Assert.Equal($"workspaces/{vmId}/g2", recovered.WorkBranch);
        var source = Assert.Single(runtime.VerifiedRecoverySources);
        Assert.Equal(saved.VerifiedRemoteRef, source.RemoteRef);
        Assert.Equal(saved.VerifiedRemoteCommit, source.Commit);
        Assert.Null(cloud.Volume);
        Assert.Equal(0, cloud.VolumeCreates);
        Assert.Equal(0, cloud.Attaches);
    }

    [Fact]
    public async Task GitSaveWithMismatchedRemoteCommitKeepsComputeReserved()
    {
        var (_, vm, _) = await SeedStop("quiesce", ephemeral: true);
        runtime.RemoteHead = new('d', 40);
        await Hop();
        var blocked = await CurrentVm(vm.Id);
        Assert.Equal("blocked", blocked.Phase);
        Assert.True(blocked.ComputeReserved);
        Assert.Null(blocked.VerifiedRemoteCommit);
        Assert.Empty(cloud.DeletedServers);
    }

    private void UseEnrollment(bool loseEnrollmentReply = false)
    {
        runtime.Enroll = async (target, enrollment) =>
        {
            var token = enrollment.GetProperty("token").GetString();
            var operation = enrollment.GetProperty("operationId").GetString()!;
            Assert.Equal("https://control.example.test/api/development-workspaces/runtime/enroll",
                enrollment.GetProperty("url").GetString());
            await using var enrollmentDb = Context();
            var controller = new DevelopmentWorkspaceEnrollmentController(enrollmentDb, Options.Create(policy), clock)
            {
                ControllerContext = new() { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers.Authorization = "Bearer " + token;
            var request = new DevelopmentWorkspaceEnrollmentRequest(1, target.VmId, target.Generation, target.Kind,
                operation, new('a', 64), JsonSerializer.SerializeToElement(new
                {
                    type = "BootstrapReady", vmId = target.VmId, generation = target.Generation,
                    kind = target.Kind, operationId = operation
                }));
            Assert.IsType<OkObjectResult>(await controller.Enroll(request, default));
            Assert.IsType<UnauthorizedResult>(await controller.Enroll(request, default));
            if (loseEnrollmentReply && target.Generation == 1)
                throw new IOException("Synthetic lost SSH reply after enrollment.");
        };

    }

    private async Task<DevelopmentWorkspaceVm> ProcessUntilAsync(string vmId, string phase)
    {
        for (var step = 0; step < 40; step++)
        {
            var vm = await CurrentVm(vmId);
            if (vm.Phase == phase) return vm;
            if (vm.OperationId is not null)
            {
                var operation = await CurrentOperation(vm.OperationId);
                Assert.True(operation.Status != "failed", operation.ErrorMessage);
                if (operation.Step == "gateway-drain") clock.Advance(TimeSpan.FromSeconds(76));
            }
            Assert.True(await Processor().ProcessNextAsync(default));
        }
        var current = await CurrentVm(vmId);
        Assert.Fail($"Expected {phase}; got {current.Phase}: {current.ErrorMessage}.");
        return current;
    }
}
