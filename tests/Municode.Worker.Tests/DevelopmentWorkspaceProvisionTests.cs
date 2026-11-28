using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Municode.Common.Services;
using Municode.Worker.DevelopmentWorkspaces;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Theory]
    [InlineData(false, "attach")]
    [InlineData(true, "bootstrap")]
    public async Task StockUbuntuVmIsProvisionedOnceFromThePinnedRecipeBeforeStorageOrBootstrap(bool ephemeral, string nextStep)
    {
        policy.Images["image"].Source = WorkspaceRecipe.UbuntuSource;
        var (_, vm, _) = await Seed("allocate", ephemeral);
        await Hop("server-ready");
        await Hop("provision");
        runtime.ProvisionStatuses.Enqueue(new("Unreachable"));
        runtime.ProvisionStatuses.Enqueue(new("NotStarted"));
        runtime.ProvisionStatuses.Enqueue(new("Running"));
        runtime.ProvisionStatuses.Enqueue(new("Provisioned"));
        await Hop("provision");
        Assert.Empty(runtime.ProvisionStarts);
        await Hop("provision");
        await Hop("provision");
        await Hop(nextStep);
        var (directory, recipe) = Assert.Single(runtime.ProvisionStarts);
        Assert.Equal(WorkspaceRecipe.Sha256, recipe);
        Assert.Equal(recipe, Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(directory, "image", "manifest.lock.json")))));
        Assert.True(File.Exists(Path.Combine(directory, "image", "build.py")));
        Assert.True(File.Exists(Path.Combine(directory, "bootstrap.py")));
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.Equal(ephemeral, (await CurrentVm(vm.Id)).Kind == "ephemeral");
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Restarted")]
    [InlineData("Deadline")]
    public async Task StockProvisioningFailureFailsTheStartWithoutBootstrapOrRestart(string failure)
    {
        policy.Images["image"].Source = WorkspaceRecipe.UbuntuSource;
        var (_, vm, operation) = await Seed("allocate", ephemeral: true);
        await Hop("server-ready");
        await Hop("provision");
        if (failure == "Restarted")
        {
            runtime.ProvisionStatuses.Enqueue(new("NotStarted"));
            await Hop("provision");
        }
        if (failure == "Deadline") clock.Advance(TimeSpan.FromMinutes(DevelopmentWorkspaceProcessor.ProvisionMinutes));
        runtime.ProvisionStatuses.Enqueue(failure == "Failed" ? new("Failed", "synthetic install failure") : new("NotStarted"));
        await Hop("provision");
        var current = await CurrentOperation(operation.Id);
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal("workspace_reconciliation_required", current.ErrorCode);
        Assert.True(runtime.ProvisionStarts.Count == (failure == "Restarted" ? 1 : 0));
        Assert.Null(runtime.BootstrapConfiguration);
        Assert.Contains(failure switch { "Failed" => "synthetic install failure", "Restarted" => "without a result",
            _ => "45 minutes" }, current.ErrorMessage);
    }

    [Theory]
    [InlineData("system", true)]
    [InlineData("snapshot", false)]
    public async Task StockImageMustBeTheProvidersStockUbuntuImage(string type, bool accepted)
    {
        policy.Images["image"].Source = WorkspaceRecipe.UbuntuSource;
        cloud.ImageType = type;
        var (_, _, operation) = await Seed("pending");
        await Hop();
        var current = await CurrentOperation(operation.Id);
        Assert.Equal(accepted, current.Step == "bootstrap-secret" && current.ErrorMessage is null);
    }
}
