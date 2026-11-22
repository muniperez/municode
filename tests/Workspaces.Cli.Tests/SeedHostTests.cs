// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Workspaces.Cli.Tests;

public sealed class SeedHostTests
{
    [Fact]
    public async Task HostFingerprintMustMatchIndependentConsoleFingerprint()
    {
        var key = HostKey();
        var line = "203.0.113.10 ssh-ed25519 " + key;
        var fingerprint = SeedHost.Fingerprint(line, "203.0.113.10");
        using var fixture = new HostFixture();
        var process = new ProcessFixture(line + "\n");
        var host = new SeedHost(process);
        var state = State();
        await host.TrustAsync(state, fixture.Root, fingerprint, CancellationToken.None);
        Assert.Equal(fingerprint, state.HostFingerprint);
        Assert.Equal(line + "\n", File.ReadAllText(Path.Combine(fixture.Root, "known_hosts")));

        await Assert.ThrowsAsync<CliCommandException>(() =>
            new SeedHost(process).TrustAsync(state, fixture.Root,
                "SHA256:" + Convert.ToBase64String(new byte[32]), CancellationToken.None));
    }

    [Fact]
    public void FingerprintRejectsWrongAddressOrAlgorithm()
    {
        var line = "203.0.113.10 ssh-ed25519 " + HostKey();
        Assert.Throws<CliCommandException>(() => SeedHost.Fingerprint(line, "203.0.113.11"));
        Assert.Throws<CliCommandException>(() =>
            SeedHost.Fingerprint(line.Replace("ssh-ed25519", "ssh-rsa"), "203.0.113.10"));
    }

    [Fact]
    public async Task InterruptedTokenDownloadUsesAtomicCompleteFileOnly()
    {
        using var fixture = new HostFixture();
        var release = Path.Combine(fixture.Root, "release");
        SeedFiles.CreateDirectory(release);
        foreach (var name in new[]
                 {
                     "release-manifest.json",
                     "municode-workspaces-1.2.3-linux-any.tar.gz",
                     "checksums.txt",
                     "checksums.txt.sigstore.json"
                 })
            SeedFiles.WriteNew(Path.Combine(release, name), "fixture"u8);
        var state = State();
        state.HostFingerprint = "SHA256:" + new string('A', 43);
        state.InstallationId = "workspaces-" + state.Id;
        var process = new DownloadProcess(
            "a"u8.ToArray().Concat(Enumerable.Repeat((byte)'a', 63)).ToArray());
        var host = new SeedHost(process);
        await host.InstallAsync(state, fixture.Root, release, CancellationToken.None);
        Assert.Equal(new string('a', 64),
            File.ReadAllText(Path.Combine(fixture.Root, "setup-token")).Trim());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "setup-token.pending")));
        var extraction = Assert.Single(process.Commands, command =>
            command.Contains("sha256sum --check --strict", StringComparison.Ordinal));
        Assert.Contains("extracted.pending", extraction);
        Assert.Contains("/.verified", extraction);
        Assert.DoesNotContain("if [ -d ", extraction);

        using var malformed = new HostFixture();
        var badHost = new SeedHost(new DownloadProcess("partial"u8.ToArray()));
        await Assert.ThrowsAsync<CliCommandException>(() =>
            badHost.InstallAsync(state, malformed.Root, release, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(malformed.Root, "setup-token")));
    }

    [Fact]
    public async Task DownloadedTokenMustHashToExactRemoteBytes()
    {
        using var fixture = new HostFixture();
        var release = ReleaseFiles(fixture.Root);
        var state = State();
        state.HostFingerprint = "SHA256:" + new string('A', 43);
        state.InstallationId = "workspaces-" + state.Id;
        var expected = new string('a', 64);
        var transfer = new string('b', 64);
        var host = new SeedHost(new DownloadProcess(Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(transfer)));
        await Assert.ThrowsAsync<CliCommandException>(() =>
            host.InstallAsync(state, fixture.Root, release, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "setup-token")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "setup-token.pending")));
    }

    [Fact]
    public async Task DownloadRejectsTrimmedButNoncanonicalTokenBytes()
    {
        using var fixture = new HostFixture();
        var release = ReleaseFiles(fixture.Root);
        var state = State();
        state.HostFingerprint = "SHA256:" + new string('A', 43);
        state.InstallationId = "workspaces-" + state.Id;
        var expected = Encoding.UTF8.GetBytes(new string('a', 64));
        var noncanonical = "\n".ToCharArray().Concat(new string('a', 64)).Select(character => (byte)character)
            .ToArray();
        var host = new SeedHost(new DownloadProcess(expected, noncanonical, true));
        await Assert.ThrowsAsync<CliCommandException>(() =>
            host.InstallAsync(state, fixture.Root, release, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "setup-token")));
    }

    private static string ReleaseFiles(string root)
    {
        var release = Path.Combine(root, "release");
        SeedFiles.CreateDirectory(release);
        foreach (var name in new[]
                 {
                     "release-manifest.json",
                     "municode-workspaces-1.2.3-linux-any.tar.gz",
                     "checksums.txt",
                     "checksums.txt.sigstore.json"
                 })
            SeedFiles.WriteNew(Path.Combine(release, name), "fixture"u8);
        return release;
    }

    private static string HostKey()
    {
        var key = new byte[51];
        key[3] = 11;
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(key, 4);
        key[18] = 32;
        for (var index = 19; index < key.Length; index++) key[index] = (byte)index;
        return Convert.ToBase64String(key);
    }

    private static SeedState State() => new()
    {
        Id = new string('a', 32),
        Plan = new(
            new("hetzner", "nbg1", "cx22", "console.example.com", "api.example.com",
                "workspaces.example.net", "workspaces-v1.2.3"),
            new("account", "scope", "image", "linux/amd64", 2, 4096, 40,
                [new("vps", 10m, 0.01m, "EUR", "hourly", "none", "fixture")],
                DateTimeOffset.UtcNow, "fixture"),
            new("workspaces-v1.2.3", "municode-workspaces", "https://github.com/muniperez/municode",
                new string('a', 40), "municode-workspaces-1.2.3-linux-any.tar.gz",
                new string('b', 64), new string('c', 64), "github-sigstore")),
        Server = new("server", new string('a', 32), "nbg1", "cx22", "image", "203.0.113.10")
    };

    private sealed class ProcessFixture(string output) : ISeedProcess
    {
        public Task<SeedProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            byte[]? input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SeedProcessResult(0, output));
    }

    private sealed class DownloadProcess(
        byte[] token,
        byte[]? transfer = null,
        bool transferIsRaw = false) : ISeedProcess
    {
        public List<string> Commands { get; } = [];

        public Task<SeedProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            byte[]? input,
            CancellationToken cancellationToken)
        {
            if (fileName == "ssh")
                Commands.Add(arguments[^1]);
            if (fileName == "ssh" && arguments[^1].Contains("sha256sum ", StringComparison.Ordinal))
            {
                var remoteBytes = token.Concat("\n"u8.ToArray()).ToArray();
                return Task.FromResult(new SeedProcessResult(0,
                    Convert.ToHexStringLower(SHA256.HashData(remoteBytes)) + "\n"));
            }
            if (fileName == "scp")
            {
                var destination = arguments[^1];
                if (!destination.StartsWith("root@", StringComparison.Ordinal))
                {
                    SeedFiles.WriteNew(destination,
                        transferIsRaw ? transfer!
                            : (transfer ?? token).Concat("\n"u8.ToArray()).ToArray());
                }
            }
            return Task.FromResult(new SeedProcessResult(0, ""));
        }
    }

    private sealed class HostFixture : IDisposable
    {
        public HostFixture()
        {
            Root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "artifacts", "seed-tests",
                "host-" + Guid.NewGuid().ToString("N"));
            SeedFiles.CreateDirectory(Root);
        }

        public string Root { get; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
