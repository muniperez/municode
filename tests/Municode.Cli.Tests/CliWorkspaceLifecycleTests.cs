// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli;
using Municode.Cli.Commands;

namespace Municode.Cli.Tests;

public sealed class CliWorkspaceLifecycleTests
{
    [Fact]
    public async Task MultipleOrganizationsRequireSelectionBeforeWorkspaceRequest()
    {
        using var context = new CliTestContext();
        context.Handler.Organizations = ["org", "other"];
        Assert.Equal(CliExitCodes.ValidationError, await context.Run("list"));
        Assert.Single(context.Handler.Requests);
        Assert.Equal("/api/me", context.Handler.Requests[0].Path);
        Assert.Contains("accessible organization", context.Console.Error);
    }

    [Fact]
    public async Task CreateAndInspectAreExplicitAndNeverStartCompute()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run("create", "--organization", "org",
            "--name", " Coding ", "--image", "image", "--profile", "Standard", "--plan", "plan",
            "--volume-gb", "20"));
        var create = context.Handler.Requests.Last();
        Assert.Equal("/api/development-workspaces", create.Path);
        Assert.Equal("Coding", create.Body!.Value.GetProperty("name").GetString());
        Assert.Equal(
            CliApplication.DefaultWorkspaceCreateKey("org", "Coding"),
            create.Body.Value.GetProperty("idempotencyKey").GetString());
        Assert.False(create.Body.Value.TryGetProperty("startPrimaryVm", out _));
        Assert.False(create.Body.Value.TryGetProperty("keepRunning", out _));

        Assert.Equal(CliExitCodes.Success, await context.Run("status", "workspace", "--organization", "org"));
        Assert.Equal(CliExitCodes.Success, await context.Run("vms", "workspace", "--organization", "org"));
        Assert.DoesNotContain(context.Handler.Requests, request => request.Path.Contains("/start", StringComparison.Ordinal));
        Assert.Contains("retained primary volume", context.Console.Output);
        Assert.Contains("Git-only", context.Console.Output);
        Assert.Contains("generation=3", context.Console.Output);
        Assert.DoesNotContain("synthetic-token", context.Console.Output);
    }

    [Fact]
    public async Task StatusShowsCheckoutCheckpointAndOffHostBackupFreshness()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run("status", "workspace", "--organization", "org"));
        Assert.DoesNotContain("Checkouts:", context.Console.Output);
        Assert.Contains("Off-host backup: none verified (status unavailable).", context.Console.Output);

        using var local = new CliTestContext();
        local.Handler.StorageLayout = 2;
        local.Handler.LastCheckpointAt = "2026-11-21T20:05:00+00:00";
        local.Handler.CheckpointStatus = "Stale";
        local.Handler.LastBackupAt = "2026-11-20T03:00:00+00:00";
        local.Handler.BackupStatus = "Fresh";
        Assert.Equal(CliExitCodes.Success, await local.Run("status", "workspace", "--organization", "org"));
        Assert.Contains("Checkouts: on the VM disk; last saved to retained storage 2026-11-21T20:05:00.0000000+00:00 (Stale).",
            local.Console.Output);
        Assert.Contains("Off-host backup: last verified 2026-11-20T03:00:00.0000000+00:00 (Fresh).", local.Console.Output);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("cancel-idle-stop")]
    public async Task WorkspaceControlsUseAdaptedBackendRoutes(string action)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run(action, "workspace", "--organization", "org"));
        Assert.Equal(
            $"/api/development-workspaces/workspace/{action}?organizationId=org",
            context.Handler.Requests.Last().Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--confirm-permanent-deletion=false")]
    [InlineData("--confirm-permanent-deletion false")]
    public async Task DeleteRequiresStandaloneConfirmationBeforeAnyRequest(string confirmation)
    {
        using var context = new CliTestContext();
        var options = new List<string> { "delete", "workspace", "--organization", "org" };
        options.AddRange(confirmation.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(CliExitCodes.ValidationError, await context.Run([.. options]));
        Assert.Empty(context.Handler.Requests);
        Assert.Contains("--confirm-permanent-deletion", context.Console.Error);
    }

    [Theory]
    [InlineData("deleting", "deleting", "Permanent deletion is in progress")]
    [InlineData("deleted", "retained", "Workspace permanently deleted")]
    public async Task DeleteCallsConfirmedBackendAndShowsPhase(string phase, string volume, string message)
    {
        using var context = new CliTestContext();
        context.Handler.WorkspacePhase = phase;
        context.Handler.VolumeStatus = volume;
        Assert.Equal(CliExitCodes.Success, await context.Run(
            "delete", "work/space", "--confirm-permanent-deletion", "--organization", "org"));
        var request = context.Handler.Requests.Last();
        Assert.Equal("DELETE", request.Method);
        Assert.Equal(
            "/api/development-workspaces/work%2Fspace?organizationId=org&confirmPermanentDeletion=true",
            request.Path);
        Assert.False(request.HasBody);
        Assert.Contains("IRREVERSIBLE", context.Console.Output);
        Assert.Contains(message, context.Console.Output);
    }

    [Fact]
    public async Task JsonInspectionKeepsStdoutMachineReadable()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success,
            await context.Run("status", "workspace", "--organization", "org", "--json"));
        using var document = JsonDocument.Parse(Assert.Single(context.Console.Messages));
        Assert.Equal("workspace", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("primary", document.RootElement.GetProperty("vms")[0].GetProperty("id").GetString());
        Assert.All(context.Handler.Requests, request => Assert.Equal("Bearer synthetic-token", request.Authorization));
    }
}
