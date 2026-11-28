// SPDX-License-Identifier: MPL-2.0

using Municode.Cli;
using Municode.Cli.Commands;

namespace Municode.Cli.Tests;

public sealed class CliHelpConfigAuthTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpShowsOnlyStandaloneMunicodeSurface(string command)
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success, await context.Application.RunAsync([command], CancellationToken.None));
        Assert.Contains("Municode CLI version", context.Console.Output);
        Assert.Contains("workspaces", context.Console.Output);
        Assert.Contains("MUNICODE_API_URL", context.Console.Output);
        Assert.DoesNotContain("Minicloud", context.Console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceHelpCoversLifecycleInspectionAndSafety()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success,
            await context.Application.RunAsync(["help", "workspaces"], CancellationToken.None));
        Assert.Contains("workspaces create", context.Console.Output);
        Assert.Contains("workspaces status", context.Console.Output);
        Assert.Contains("vm-start", context.Console.Output);
        Assert.Contains("--confirm-permanent-deletion", context.Console.Output);
        Assert.Contains("--acknowledge-ignored-data-loss", context.Console.Output);
    }

    [Fact]
    public void ConfigIsPersistedIndependentlyFromEnvironmentOverride()
    {
        using var context = new CliTestContext();
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".local", "artifacts", "issue7",
            Guid.NewGuid().ToString("N"));
        var environment = CliEnvironment.ForTests("", directory);
        Assert.Equal("https://workspaces.example", CliEnvironment.NormalizeUrl("workspaces.example"));
        environment.SetApiBaseUrl("https://workspaces.example");
        var reloaded = CliEnvironment.ForTests("", directory);
        Assert.Equal("https://workspaces.example", reloaded.ApiBaseUrl);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task TokenValidationUsesMunicodeMeRouteAndBearerToken()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success,
            await context.Application.RunAsync(["token", "set", "new-synthetic-token"], CancellationToken.None));
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal("/api/me", request.Path);
        Assert.Equal("Bearer new-synthetic-token", request.Authorization);
        Assert.Equal("new-synthetic-token", context.Tokens.GetToken());
        Assert.Contains("Token stored", context.Console.Output);
    }

    [Fact]
    public async Task BrowserLoginExchangesApprovedSessionWithoutSendingStoredToken()
    {
        using var context = new CliTestContext();
        Assert.Equal(CliExitCodes.Success,
            await context.Application.RunAsync(["login", "--no-browser"], CancellationToken.None));
        Assert.Collection(context.Handler.Requests,
            request =>
            {
                Assert.Equal("/api/cli-login-sessions", request.Path);
                Assert.Null(request.Authorization);
            },
            request =>
            {
                Assert.Equal("/api/cli-login-sessions/cli-login/exchange", request.Path);
                Assert.Null(request.Authorization);
            });
        Assert.Equal("synthetic-browser-token", context.Tokens.GetToken());
        Assert.Contains("Logged in as dev@example.com", context.Console.Output);
    }
}
