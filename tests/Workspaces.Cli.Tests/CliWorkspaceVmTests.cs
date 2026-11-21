// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli;

namespace Workspaces.Cli.Tests;

public sealed class CliWorkspaceVmTests
{
    [Theory]
    [InlineData("vm-start", "start")]
    [InlineData("vm-stop", "stop")]
    [InlineData("vm-cancel-idle-stop", "cancel-idle-stop")]
    public async Task VmControlsBindGenerationAndVm(string command, string operation)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.ValidationError,
            await context.Run(command, "workspace", "--organization", "org", "--vm", "ephemeral"));
        Assert.Equal(CliExitCodes.Success, await context.Run(command, "workspace", "--organization", "org",
            "--vm", "ephemeral", "--generation", "0"));
        var request = context.Handler.Requests.Last();
        Assert.Equal(
            $"/api/development-workspaces/workspace/vms/ephemeral/{operation}?organizationId=org",
            request.Path);
        Assert.Equal(0, request.Body!.Value.GetProperty("generation").GetInt64());
    }

    [Theory]
    [InlineData("--repository", "https://git.example/other.git")]
    [InlineData("--base-ref", "refs/heads/other")]
    [InlineData("--base-commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task VmStartRejectsSourceOverrides(string option, string value)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.ValidationError, await context.Run("vm-start", "workspace",
            "--organization", "org", "--vm", "ephemeral", "--generation", "3", option, value));
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task LaunchRequiresBothPoliciesAndPreservesResolvedGitSource()
    {
        using var context = new CliTestContext();
        var options = new[]
        {
            "launch", "workspace", "--organization", "org", "--image", "image", "--profile", "Heavy",
            "--plan", "plan", "--repository", "https://git.example/repo.git",
            "--base-ref", "refs/heads/main", "--base-commit", new string('a', 40),
            "--idempotency-key", "launch-once"
        };
        Assert.Equal(CliExitCodes.ValidationError, await context.Run(options));
        Assert.Equal(CliExitCodes.ValidationError, await context.Run([.. options, "--push-authorized"]));
        Assert.Equal(CliExitCodes.Success, await context.Run(
            [.. options, "--push-authorized", "--acknowledge-ignored-data-loss"]));
        var request = context.Handler.Requests.Last();
        Assert.Equal("/api/development-workspaces/workspace/vms/ephemeral?organizationId=org", request.Path);
        Assert.True(request.Body!.Value.GetProperty("pushAuthorized").GetBoolean());
        Assert.True(request.Body.Value.GetProperty("acknowledgeIgnoredDataLoss").GetBoolean());
        Assert.Equal(new string('a', 40), request.Body.Value.GetProperty("baseCommit").GetString());
    }

    [Fact]
    public async Task DiscardRequiresGenerationAndSeparateLossAcknowledgements()
    {
        using var context = new CliTestContext();
        var options = new[]
        {
            "vm-discard", "workspace", "--organization", "org", "--vm", "ephemeral", "--generation", "3"
        };
        Assert.Equal(CliExitCodes.ValidationError, await context.Run(options));
        Assert.Equal(CliExitCodes.ValidationError, await context.Run([.. options, "--discard-local-data"]));
        Assert.Equal(CliExitCodes.Success, await context.Run(
            [.. options, "--discard-local-data", "--acknowledge-ignored-data-loss"]));
        var body = context.Handler.Requests.Last().Body!.Value;
        Assert.Equal(3, body.GetProperty("generation").GetInt64());
        Assert.True(body.GetProperty("discardLocalData").GetBoolean());
        Assert.True(body.GetProperty("acknowledgeIgnoredDataLoss").GetBoolean());
        Assert.Contains("IRREVERSIBLE", context.Console.Output);
    }

    [Theory]
    [InlineData("resize-next-start", "resize")]
    [InlineData("vm-resize-next-start", "vms/ephemeral/resize")]
    public async Task ResizeIsNextStartOnlyAndGenerationBound(string command, string route)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run(command, "workspace", "--organization", "org",
            "--vm", "ephemeral", "--generation", "3", "--image", "image", "--profile", "Heavy",
            "--plan", "heavy-plan"));
        var request = context.Handler.Requests.Last();
        Assert.Equal($"/api/development-workspaces/workspace/{route}?organizationId=org", request.Path);
        Assert.Equal(3, request.Body!.Value.GetProperty("generation").GetInt64());
        Assert.Equal("heavy-plan", request.Body.Value.GetProperty("planId").GetString());
    }

    [Theory]
    [InlineData("task-renew", "PUT", "lease")]
    [InlineData("task-complete", "POST", "complete")]
    public async Task TaskLeaseUsesVmGenerationAndExplicitCompletion(string command, string method, string operation)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run(command, "workspace", "--organization", "org",
            "--vm", "ephemeral", "--generation", "3", "--task", "build", "--cancelled"));
        var request = context.Handler.Requests.Last();
        Assert.Equal(method, request.Method);
        Assert.Equal(
            $"/api/development-workspaces/workspace/vms/ephemeral/tasks/build/{operation}?organizationId=org",
            request.Path);
        Assert.Equal(3, request.Body!.Value.GetProperty("generation").GetInt64());
        if (command == "task-complete")
            Assert.True(request.Body.Value.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task OpenMintsGatewayLinkForReadyVmWithoutStarting()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Run(
            "open", "workspace", "--organization", "org", "--no-browser"));
        var request = context.Handler.Requests.Last();
        Assert.Equal(
            "/api/development-workspaces/workspace/vms/primary/launch?organizationId=org",
            request.Path);
        Assert.Equal(3, request.Body!.Value.GetProperty("generation").GetInt64());
        Assert.Contains(RecordingHandler.LaunchUrl, context.Console.Output);
        Assert.DoesNotContain(context.Handler.Requests, item => item.Path.Contains("/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenRejectsUnsafeUrl()
    {
        using var context = new CliTestContext();
        context.Handler.UnsafeLaunch = true;
        Assert.Equal(CliExitCodes.NetworkOrApiUnavailable, await context.Run(
            "open", "workspace", "--organization", "org", "--no-browser"));
        Assert.DoesNotContain("http://unsafe.example", context.Console.Output);
    }
}
