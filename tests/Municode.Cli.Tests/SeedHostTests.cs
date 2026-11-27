// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class SeedHostTests
{
    [Theory]
    [InlineData("set -eu\n[ -z \"${MUNICODE_SEED_INHERITED+x}\" ]\nprintf '%s\\n' \"$HOME\"\n[ -z \"${MUNICODE_SEED_INHERITED+x}\" ]\nexit 37", 37, "/root\n")]
    [InlineData("cd / && [ -z \"${MUNICODE_SEED_INHERITED+x}\" ] && printf '%s\\n' \"$HOME\"; [ -z \"${MUNICODE_SEED_INHERITED+x}\" ]", 0, "/root\n")]
    [InlineData("set -e\nfalse\nprintf 'must not run'", 1, "")]
    public async Task PinnedSshRunsEntireScriptInSanitizedShell(
        string command, int exitCode, string output)
    {
        if (OperatingSystem.IsWindows()) return;
        var process = new LocalSshProcess();
        var state = State();
        state.HostFingerprint = "SHA256:" + new string('A', 43);

        var result = await new SeedHost(process).SshAsync(
            state, "unused", command, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, result.Output);
        Assert.Contains("StrictHostKeyChecking=yes", process.Arguments);
        Assert.Contains("IdentitiesOnly=yes", process.Arguments);
        Assert.Contains("ForwardAgent=no", process.Arguments);
        Assert.Contains("UserKnownHostsFile=" + Path.Combine("unused", "known_hosts"), process.Arguments);
        Assert.Equal("root@203.0.113.10", process.Arguments[^2]);
    }

    [Fact]
    public async Task PinnedSshPreservesMultilineScriptAndQuotedPaths()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new HostFixture();
        var path = Path.Combine(fixture.Root, "quoted ' path $HOME `false`");
        SeedFiles.CreateDirectory(path);
        var state = State();
        state.HostFingerprint = "SHA256:" + new string('A', 43);
        var command = "set -eu\ncd " + SeedGuard.SafeShellQuote(path)
            + "\nvalue='literal $HOME and \"quotes\"'\nprintf '%s\\n%s\\n' \"$PWD\" \"$value\"";

        var result = await new SeedHost(new LocalSshProcess()).SshAsync(
            state, fixture.Root, command, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(path + "\nliteral $HOME and \"quotes\"\n", result.Output);
    }

    [Fact]
    public async Task RealSshKeygenPublicModeIsAcceptedWithoutRelaxingPrivateFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new HostFixture();
        var key = Path.Combine(fixture.Root, "id_ed25519");
        var host = new SeedHost(new RealKeygenProcess());
        await host.GenerateSshKeyAsync(fixture.Root, CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(key + ".pub"));

        await host.GenerateSshKeyAsync(fixture.Root, CancellationToken.None);
        Assert.Throws<CliCommandException>(() => SeedFiles.Check(key + ".pub"));

        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await Assert.ThrowsAsync<CliCommandException>(() =>
            host.GenerateSshKeyAsync(fixture.Root, CancellationToken.None));
    }

    [Theory]
    [InlineData("writable")]
    [InlineData("link")]
    [InlineData("directory")]
    [InlineData("mismatch")]
    public async Task GeneratedPublicKeyStillRequiresSafePathAndMatchingKey(string invalid)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new HostFixture();
        var host = new SeedHost();
        await host.GenerateSshKeyAsync(fixture.Root, CancellationToken.None);
        var publicKey = Path.Combine(fixture.Root, "id_ed25519.pub");
        switch (invalid)
        {
            case "writable":
                File.SetUnixFileMode(publicKey, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);
                break;
            case "link":
                File.Move(publicKey, publicKey + ".original");
                File.CreateSymbolicLink(publicKey, publicKey + ".original");
                break;
            case "directory":
                File.Delete(publicKey);
                SeedFiles.CreateDirectory(publicKey);
                break;
            case "mismatch":
                File.WriteAllText(publicKey, "ssh-ed25519 " + HostKey() + "\n");
                break;
        }

        await Assert.ThrowsAsync<CliCommandException>(() =>
            host.GenerateSshKeyAsync(fixture.Root, CancellationToken.None));
    }

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
                     "municode-1.2.3-linux-any.tar.gz",
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
                     "municode-1.2.3-linux-any.tar.gz",
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
                "workspaces.example.net", "municode-v1.2.3"),
            new("account", "scope", "image", "linux/amd64", 2, 4096, 40,
                [new("vps", 10m, 0.01m, "EUR", "hourly", "none", "fixture")],
                DateTimeOffset.UtcNow, "fixture"),
            new("municode-v1.2.3", "municode", "https://github.com/muniperez/municode",
                new string('a', 40), "municode-1.2.3-linux-any.tar.gz",
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

    private sealed class RealKeygenProcess : ISeedProcess
    {
        public Task<SeedProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            byte[]? input,
            CancellationToken cancellationToken)
        {
            Assert.Equal("ssh-keygen", fileName);
            return SeedProcess.Default.RunAsync("/bin/sh",
                ["-c", "umask 022\nssh-keygen " + string.Join(" ", arguments.Select(SeedGuard.SafeShellQuote))],
                input, cancellationToken);
        }
    }

    private sealed class LocalSshProcess : ISeedProcess
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<SeedProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            byte[]? input,
            CancellationToken cancellationToken)
        {
            Assert.Equal("ssh", fileName);
            Arguments = arguments;
            return SeedProcess.Default.RunAsync("/bin/sh",
                ["-c", "export MUNICODE_SEED_INHERITED=inherited\n" + arguments[^1]],
                input, cancellationToken);
        }
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
            Root = SeedTestDirectory.Create("host");
        }

        public string Root { get; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
