// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Commands;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class CliInstanceTests
{
    [Theory]
    [InlineData("control-alias", "control-alias", null)]
    [InlineData("ops@control.example.com", "ops@control.example.com", null)]
    [InlineData("ops@control.example.com:2222", "ops@control.example.com", "2222")]
    [InlineData("ssh://ops@[2001:db8::1]:2222", "ops@2001:db8::1", "2222")]
    public async Task AddPinsKeyBeforeAuthenticatingAndShowProbesLive(string destination, string host, string? port)
    {
        using var fixture = new Fixture();
        var identity = Path.Combine(fixture.Root, "identity file");
        SeedFiles.WriteNew(identity, "synthetic-private-key"u8);
        Assert.Equal(CliExitCodes.Success, await fixture.Run("add", "production", "--ssh", destination,
            "--identity-file", identity, "--confirm-host-fingerprint", FakeInstanceSsh.Fingerprint, "--json"));
        using (var json = JsonDocument.Parse(fixture.Console.Messages.Last()))
            Assert.Equal(destination, json.RootElement.GetProperty("ssh").GetProperty("destination").GetString());
        var (discovery, probe) = (fixture.Ssh.Calls[0], fixture.Ssh.Calls[1]);
        Assert.Contains("StrictHostKeyChecking=accept-new", discovery);
        Assert.Contains("PreferredAuthentications=none", discovery);
        Assert.DoesNotContain("-i", discovery);
        Assert.Contains("StrictHostKeyChecking=yes", probe);
        Assert.Contains("ForwardAgent=no", probe);
        Assert.Contains("GlobalKnownHostsFile=none", probe);
        Assert.Contains("ControlPath=none", probe);
        Assert.Equal(identity, probe[Array.IndexOf(probe, "-i") + 1]);
        foreach (var call in fixture.Ssh.Calls)
        {
            Assert.Equal(host, call[^2]);
            if (port is null) Assert.DoesNotContain("-p", call);
            else Assert.Equal(port, call[Array.IndexOf(call, "-p") + 1]);
        }

        var entry = Path.Combine(fixture.Root, "instances", "production");
        SeedFiles.Check(entry, true);
        foreach (var path in Directory.GetFiles(entry))
        {
            SeedFiles.Check(path);
            Assert.DoesNotContain("synthetic-private-key", File.ReadAllText(path));
        }
        Assert.Equal(2, Directory.GetFiles(entry).Length);
        Assert.DoesNotContain("synthetic-private-key", fixture.Console.Output);
        fixture.Ssh.Tag = "municode-v1.2.4";
        Assert.Equal(CliExitCodes.Success, await fixture.Run("list", "--json"));
        Assert.Equal(2, fixture.Ssh.Calls.Count);
        using (var json = JsonDocument.Parse(fixture.Console.Messages.Last()))
            Assert.Equal("municode-v1.2.3", json.RootElement[0].GetProperty("host").GetProperty("release").GetProperty("tag").GetString());
        Assert.Equal(CliExitCodes.Success, await fixture.Run("show", "production"));
        Assert.Contains("municode-v1.2.4", fixture.Console.Output);
        Assert.Contains("StrictHostKeyChecking=yes", fixture.Ssh.Calls[2]);
        fixture.Ssh.InstallationId = "different-installation";
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("show", "production"));
        Assert.Contains("installation ID changed", fixture.Console.Error);
    }

    [Fact]
    public async Task FingerprintMismatchAndMissingNonInteractiveConfirmationNeverAuthenticate()
    {
        using var fixture = new Fixture();
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("add", "production", "--ssh", "alias"));
        Assert.Empty(fixture.Ssh.Calls);
        Assert.Contains("Non-interactive", fixture.Console.Error);
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run("add", "production", "--ssh", "alias",
            "--confirm-host-fingerprint", "SHA256:" + new string('A', 43)));
        Assert.Single(fixture.Ssh.Calls);
        Assert.Contains("not confirmed", fixture.Console.Error);
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, "instances")));
    }

    [Fact]
    public async Task InteractiveAddConfirmsFingerprintWithoutBreakingJsonOutput()
    {
        using var fixture = new Fixture();
        fixture.Console.IsInteractive = true;
        fixture.Console.Input = FakeInstanceSsh.Fingerprint;
        Assert.Equal(CliExitCodes.Success, await fixture.Run("add", "production", "--ssh", "alias", "--json"));
        Assert.Contains(FakeInstanceSsh.Fingerprint, fixture.Console.Error);
        using var output = JsonDocument.Parse(Assert.Single(fixture.Console.Messages));
        Assert.Equal("production", output.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(0, true, true, CliExitCodes.Success, null)]
    [InlineData(1000, true, true, CliExitCodes.Success, null)]
    [InlineData(1000, false, true, CliExitCodes.ValidationError, "passwordless sudo")]
    [InlineData(0, true, false, CliExitCodes.ValidationError, "reinstall with install.sh")]
    public async Task ProbeUsesNonInteractiveSudoOnlyWhenNeededAndReadsOnlyPublicMetadata(
        int uid, bool sudoAllowed, bool hasRelease, int exitCode, string? error)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var active = Path.Combine(fixture.Root, "active-release.json");
        var config = Path.Combine(fixture.Root, "operator.env");
        var sudoLog = Path.Combine(fixture.Root, "sudo-called");
        if (hasRelease) File.WriteAllText(active, JsonSerializer.Serialize(new
        {
            releaseId = "municode-v1.2.3", tag = "municode-v1.2.3", sourceRevision = new string('a', 40),
            bundleSha256 = new string('b', 64), trust = "github-release", activatedAt = "2026-11-27T18:00:00Z",
            previous = (string?)null
        }));
        File.WriteAllText(config, """
            MUNICODE_INSTALLATION_ID='test-installation'
            CONSOLE_HOSTNAME="console.example.com"
            API_HOSTNAME=api.example.com
            MUNICODE_GATEWAY_DOMAIN=workspaces.example.net
            SECRET=never-return-this
            """);
        WriteTool("id", $"echo {uid}");
        WriteTool("sudo", $"""
            [ "$1" = "-n" ] && [ "$2" = "--" ] || exit 99
            touch "{sudoLog}"
            shift 2
            {(sudoAllowed ? "exec \"$@\"" : "exit 1")}
            """);
        fixture.Ssh.Probe = (command, ct) => SeedProcess.Default.RunAsync("/bin/sh",
            ["-c", $"export PATH=\"{fixture.Root}:$PATH\"; " + command
                .Replace("/var/lib/municode/active-release.json", active, StringComparison.Ordinal)
                .Replace("/etc/municode/config/operator.env", config, StringComparison.Ordinal)], null, ct);
        Assert.Equal(exitCode, await fixture.Run("add", "production", "--ssh", "ops@host",
            "--confirm-host-fingerprint", FakeInstanceSsh.Fingerprint));
        Assert.Equal(uid != 0, File.Exists(sudoLog));
        Assert.DoesNotContain("never-return-this", fixture.Console.Output + fixture.Console.Error);
        if (error is not null) Assert.Contains(error, fixture.Console.Error);
        else Assert.Contains("test-installation", fixture.Console.Output);

        void WriteTool(string name, string body)
        {
            var path = Path.Combine(fixture.Root, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task DuplicateAndRemoveDoNotReconnectOrDeleteIdentityFile()
    {
        using var fixture = new Fixture();
        var identity = Path.Combine(fixture.Root, "identity");
        SeedFiles.WriteNew(identity, "synthetic-private-key"u8);
        string[] add = ["add", "production", "--ssh", "alias", "--identity-file", identity,
            "--confirm-host-fingerprint", FakeInstanceSsh.Fingerprint];
        Assert.Equal(CliExitCodes.Success, await fixture.Run(add));
        Assert.Equal(CliExitCodes.ValidationError, await fixture.Run(add));
        Assert.Contains("already registered", fixture.Console.Error);
        Assert.Equal(CliExitCodes.Success, await fixture.Run("remove", "production", "--json"));
        Assert.True(File.Exists(identity));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "instances", "production")));
        Assert.Equal(CliExitCodes.Success, await fixture.Run("list", "--json"));
        Assert.Equal("[]", fixture.Console.Messages.Last());
        Assert.Equal(2, fixture.Ssh.Calls.Count);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = SeedTestDirectory.Create("instance registry");
        public TestConsole Console { get; } = new();
        public FakeInstanceSsh Ssh { get; } = new();
        private readonly CliApplication _application;

        public Fixture()
        {
            var environment = CliEnvironment.ForTests("https://api.example.com", Root);
            var tokens = new TokenStore(environment);
            var client = new MunicodeApiClient(environment, tokens, new HttpClient(new RecordingHandler()));
            _application = new CliApplication(Console, environment, tokens, client, new SeedRuntime(), Ssh);
        }

        public Task<int> Run(params string[] args) =>
            _application.RunAsync(["instance", .. args], CancellationToken.None);

        public void Dispose() => Directory.Delete(Root, true);
    }
}

internal sealed class FakeInstanceSsh : ISeedProcess
{
    private const string PublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4f";
    public static string Fingerprint { get; } =
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(PublicKey))).TrimEnd('=');

    public List<string[]> Calls { get; } = [];
    public string InstallationId { get; set; } = "test-installation";
    public string Tag { get; set; } = "municode-v1.2.3";
    public Func<string, CancellationToken, Task<SeedProcessResult>>? Probe { get; set; }

    public static string KnownHost(string host) => $"{host} ssh-ed25519 {PublicKey}\n";

    public Task<SeedProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, byte[]? input,
        CancellationToken cancellationToken)
    {
        Assert.Equal("ssh", fileName);
        Calls.Add(arguments.ToArray());
        var knownHosts = arguments.Single(arg => arg.StartsWith("UserKnownHostsFile=", StringComparison.Ordinal))
            .Split('=', 2)[1].Trim('"').Replace("\\\\", "\\", StringComparison.Ordinal);
        if (arguments.Contains("StrictHostKeyChecking=accept-new"))
        {
            Assert.Empty(File.ReadAllText(knownHosts));
            File.WriteAllText(knownHosts, KnownHost(InstancePin.Alias), Encoding.ASCII);
            return Task.FromResult(new SeedProcessResult(255, ""));
        }
        Assert.Equal(KnownHost(InstancePin.Alias), File.ReadAllText(knownHosts));
        if (Probe is not null) return Probe(arguments[^1], cancellationToken);
        var release = new InstanceRelease(Tag, Tag, new string('a', 40), new string('b', 64),
            "github-release", DateTimeOffset.UnixEpoch);
        var host = new InstanceHost(InstallationId, "console.example.com", "api.example.com",
            "workspaces.example.net", release);
        return Task.FromResult(new SeedProcessResult(0, JsonSerializer.Serialize(host, SeedJournal.Json)));
    }
}
