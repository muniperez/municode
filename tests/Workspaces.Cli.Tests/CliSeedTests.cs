// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Workspaces.Cli.Tests;

public sealed class CliSeedTests
{
    [Theory]
    [InlineData("seed")]
    [InlineData("workspaces")]
    public async Task ExactSeedAliasesPlanWithoutProviderMutation(string command)
    {
        using var fixture = new SeedCliFixture();
        var args = SeedPlanArgs(fixture.CredentialPath);
        if (command == "workspaces") args = ["workspaces", "seed", .. args.Skip(1)];
        Assert.Equal(CliExitCodes.Success,
            await fixture.Application.RunAsync(args, CancellationToken.None));
        Assert.Equal(0, fixture.Providers.CreateCalls);
        Assert.Contains("No provider mutations.", fixture.Console.Output);
        Assert.Contains("one control-plane VPS", fixture.Console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Approval digest:", fixture.Console.Output);
        var operation = Regex.Match(fixture.Console.Output,
            @"Seed operation: ([0-9a-f]{32})").Groups[1].Value;
        var journal = File.ReadAllText(Path.Combine(
            fixture.Environment.ConfigHome, "seed", operation, "operation.json"));
        Assert.DoesNotContain(SeedCliFixture.Credential, journal, StringComparison.Ordinal);
        Assert.Contains("credentialScope", journal);
    }

    [Fact]
    public async Task InterruptedCreationIsReconciledWithoutSecondCreateAndResumesOnce()
    {
        using var fixture = new SeedCliFixture();
        var operation = await fixture.PlanAsync();
        var approval = fixture.Approval();
        fixture.Providers.FailNextCreate = true;

        Assert.Equal(CliExitCodes.NetworkOrApiUnavailable, await fixture.Application.RunAsync(
        [
            "seed", "apply", operation, "--credential-file", fixture.CredentialPath,
            "--approve", approval
        ], CancellationToken.None));
        Assert.Equal(1, fixture.Providers.CreateCalls);
        using (var journal = new SeedJournal(fixture.Environment.ConfigHome, operation))
            Assert.Equal("reconciliation_required", journal.Load().Phase);

        fixture.Providers.FailNextCreate = false;
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Application.RunAsync(
        [
            "seed", "resume", operation, "--credential-file", fixture.CredentialPath
        ], CancellationToken.None));
        Assert.Equal(1, fixture.Providers.CreateCalls);
        Assert.Contains("no second create", fixture.Console.Output);
        Assert.Contains("DNS record: console.example.com", fixture.Console.Output);

    }

    [Fact]
    public async Task ResumePinsSshThenInstallsVerifiesAndDoesNotRegisterProviderCredential()
    {
        using var fixture = new SeedCliFixture();
        var operation = await fixture.PlanAsync();
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Application.RunAsync(
        [
            "workspaces", "seed", "apply", operation,
            "--credential-file", fixture.CredentialPath, "--approve", fixture.Approval()
        ], CancellationToken.None));
        Assert.Equal(1, fixture.Providers.CreateCalls);
        Assert.Contains("confirm-console-fingerprint", fixture.Console.Output);

        Assert.Equal(CliExitCodes.Success, await fixture.Application.RunAsync(
        [
            "seed", "resume", operation, "--credential-file", fixture.CredentialPath,
            "--confirm-console-fingerprint", "SHA256:synthetic-fingerprint"
        ], CancellationToken.None));
        Assert.Equal(1, fixture.Providers.CreateCalls);
        Assert.Equal(1, fixture.Host.InstallCalls);
        Assert.Equal(1, fixture.Host.HealthCalls);
        Assert.DoesNotContain(SeedCliFixture.Credential, fixture.Host.UploadedText, StringComparison.Ordinal);
        Assert.Contains("never", fixture.Console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created no coding VM", fixture.Console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://console.example.com/setup", fixture.Console.Output);
    }

    [Fact]
    public async Task AChangedApprovalQuoteBlocksVpsCreation()
    {
        using var fixture = new SeedCliFixture();
        var operation = await fixture.PlanAsync();
        var original = fixture.Approval();
        fixture.Providers.Monthly = 99m;
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Application.RunAsync(
        [
            "seed", "apply", operation, "--credential-file", fixture.CredentialPath,
            "--approve", original
        ], CancellationToken.None));
        Assert.Equal(0, fixture.Providers.CreateCalls);
        Assert.NotEqual(original, fixture.Approval());
        Assert.Contains("No provider mutations", fixture.Console.Output);
    }

    [Fact]
    public async Task StatusIsLocalOnlyAndDuplicateProviderResourcesAreRejected()
    {
        using var fixture = new SeedCliFixture();
        var operation = await fixture.PlanAsync();
        fixture.Providers.DuplicateServers = true;
        var result = await fixture.Application.RunAsync(
            ["seed", "status", operation], CancellationToken.None);
        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(0, fixture.Providers.IdentityCalls);

        await Assert.ThrowsAsync<CliCommandException>(() =>
            fixture.Providers.Value.FindServerAsync(operation, CancellationToken.None));
    }

    private static string[] SeedPlanArgs(string credential) =>
    [
        "seed", "plan", "--provider", "hetzner", "--region", "nbg1", "--size", "cx22",
        "--dashboard-hostname", "console.example.com",
        "--api-hostname", "api.example.com",
        "--gateway-domain", "workspaces.example.net",
        "--release", "workspaces-v1.2.3",
        "--credential-file", credential
    ];

    private sealed class SeedCliFixture : IDisposable
    {
        private readonly string _root;

        public SeedCliFixture()
        {
            _root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "artifacts", "seed-tests",
                "municode-seed-cli-" + Guid.NewGuid().ToString("N"));
            SeedFiles.CreateDirectory(_root);
            CredentialPath = Path.Combine(_root, "provider.token");
            File.WriteAllText(CredentialPath, Credential + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(CredentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console = new TestConsole();
            Environment = CliEnvironment.ForTests("https://api.example.com", _root);
            Tokens = new TokenStore(Environment);
            Client = new MunicodeApiClient(Environment, Tokens, new HttpClient(new RecordingHandler()));
            Providers = new ProviderFixture(Credential);
            Release = new ReleaseFixture();
            Host = new HostFixture();
            Application = new CliApplication(Console, Environment, Tokens, Client, new SeedRuntime
            {
                Providers = (_, credential, _) =>
                {
                    Assert.Equal(Credential, credential);
                    return Providers.Value;
                },
                Release = () => Release,
                Host = () => Host
            });
        }

        public const string Credential = "synthetic-provider-token";
        public string CredentialPath { get; }
        public TestConsole Console { get; }
        public CliEnvironment Environment { get; }
        public TokenStore Tokens { get; }
        public MunicodeApiClient Client { get; }
        public ProviderFixture Providers { get; }
        public ReleaseFixture Release { get; }
        public HostFixture Host { get; }
        public CliApplication Application { get; }

        public async Task<string> PlanAsync()
        {
            Assert.Equal(CliExitCodes.Success,
                await Application.RunAsync(SeedPlanArgs(CredentialPath), CancellationToken.None));
            return Regex.Match(Console.Output, @"Seed operation: ([0-9a-f]{32})").Groups[1].Value;
        }

        public string Approval() => ApprovalValue.Split(' ')
            .Last(value => Regex.IsMatch(value, "^[0-9a-f]{64}$"));

        private string ApprovalValue => Console.Messages.Last(message => message.StartsWith(
            "Approval digest:", StringComparison.Ordinal));

        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class ProviderFixture(string credential)
    {
        private SeedServer? _server;
        public decimal Monthly { get; set; } = 12m;
        public bool FailNextCreate { get; set; }
        public bool DuplicateServers { get; set; }
        public int CreateCalls { get; private set; }
        public int IdentityCalls { get; private set; }
        public ISeedProvider Value => new FakeProvider(this);

        public SeedServer? Find(string operation) => DuplicateServers
            ? throw new CliCommandException(CliExitCodes.ValidationError, "duplicate")
            : _server?.OperationId == operation ? _server : null;

        public SeedServer Create(SeedState state)
        {
            CreateCalls++;
            _server = new("server-1", state.Id, "nbg1", "cx22", "ubuntu-24.04", "203.0.113.10");
            if (FailNextCreate) throw new HttpRequestException("synthetic lost response");
            return _server;
        }

        public string Scope(string operation) => SeedGuard.CredentialScope(credential, operation);
        public string Identity() { IdentityCalls++; return "synthetic-account"; }

        public SeedQuote Quote(string operation) => new(
            "synthetic-account", Scope(operation), "ubuntu-24.04", "linux/amd64",
            2, 4096, 40,
            [new("one control-plane VPS", Monthly, 0.02m, "EUR", "hourly", "tax excluded", "fixture://pricing")],
            DateTimeOffset.UtcNow, "synthetic");
    }

    private sealed class FakeProvider(ProviderFixture fixture) : ISeedProvider
    {
        public string CredentialScope(string operationId) => fixture.Scope(operationId);
        public Task<string> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fixture.Identity());
        public Task<SeedQuote> QuoteAsync(SeedSelection selection, string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(fixture.Quote(operationId));
        public Task<SeedServer?> FindServerAsync(string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(fixture.Find(operationId));
        public Task<SeedServer> CreateServerAsync(SeedState state, string publicKey, CancellationToken cancellationToken)
        {
            Assert.StartsWith("ssh-ed25519 ", publicKey);
            return Task.FromResult(fixture.Create(state));
        }
        public void Dispose() { }
    }

    private sealed class ReleaseFixture : ISeedRelease
    {
        public Task<SeedVerifiedRelease> VerifyAsync(
            string tag,
            string directory,
            CancellationToken cancellationToken)
        {
            SeedFiles.CreateDirectory(directory);
            return Task.FromResult(new SeedVerifiedRelease(Identity(tag), directory));
        }

        public static SeedReleaseIdentity Identity(string tag) => new(
            tag, "municode-workspaces", "https://github.com/muniperez/municode",
            new string('a', 40), "municode-workspaces-1.2.3-linux-any.tar.gz",
            new string('b', 64), new string('c', 64), "github-sigstore");
    }

    private sealed class HostFixture : ISeedHost
    {
        public int InstallCalls { get; private set; }
        public int HealthCalls { get; private set; }
        public string UploadedText { get; private set; } = "";
        public Task CheckPrerequisitesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task GenerateSshKeyAsync(string directory, CancellationToken cancellationToken)
        {
            SeedFiles.WriteNew(Path.Combine(directory, "id_ed25519"), Encoding.UTF8.GetBytes("private"));
            SeedFiles.WriteNew(Path.Combine(directory, "id_ed25519.pub"),
                Encoding.UTF8.GetBytes("ssh-ed25519 AAAAsynthetic key\n"));
            return Task.CompletedTask;
        }
        public Task TrustAsync(SeedState state, string directory, string independentFingerprint,
            CancellationToken cancellationToken)
        {
            state.HostFingerprint = independentFingerprint;
            return Task.CompletedTask;
        }
        public Task PrepareAsync(SeedState state, string directory, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task<bool> DnsReadyAsync(SeedState state, CancellationToken cancellationToken) =>
            Task.FromResult(true);
        public Task InstallAsync(SeedState state, string directory, string releaseDirectory,
            CancellationToken cancellationToken)
        {
            InstallCalls++;
            UploadedText = state.Plan.Selection.Provider + " " + state.Plan.Selection.ReleaseTag;
            SeedFiles.WriteNew(Path.Combine(directory, "setup-token"),
                Encoding.UTF8.GetBytes("synthetic-first-owner-token"));
            return Task.CompletedTask;
        }
        public Task VerifyHttpsAndHealthAsync(SeedState state, CancellationToken cancellationToken)
        {
            HealthCalls++;
            return Task.CompletedTask;
        }
    }
}
