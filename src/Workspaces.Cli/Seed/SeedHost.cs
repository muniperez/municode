// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;

namespace Municode.Cli.Seed;

internal sealed class SeedHost(ISeedProcess? runner = null) : ISeedHost
{
    private readonly ISeedProcess _process = runner ?? SeedProcess.Default;

    internal static string RemoteDirectory(SeedState state) => "/var/lib/municode-workspaces-seed/" + state.Id;

    public async Task CheckPrerequisitesAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in new[] { "ssh", "scp", "ssh-keygen", "ssh-keyscan" })
            await _process.RunAsync(tool, tool == "ssh-keyscan" ? ["-h"] : ["-V"],
                null, cancellationToken);
    }

    public async Task GenerateSshKeyAsync(string directory, CancellationToken cancellationToken)
    {
        var key = Path.Combine(directory, "id_ed25519");
        if (!File.Exists(key))
        {
            SeedGuard.Require(!File.Exists(key + ".pub"),
                "The generated SSH key is partially present; preserve and reconcile it.");
            await RequireAsync("ssh-keygen",
                ["-q", "-t", "ed25519", "-N", "", "-C", "municode-workspaces-seed", "-f", key],
                null, cancellationToken);
        }
        SeedFiles.Check(key);
        SeedFiles.Check(key + ".pub");
        var derived = await RequireAsync("ssh-keygen", ["-y", "-f", key], null, cancellationToken);
        SeedGuard.Require(File.ReadAllText(key + ".pub")
                .StartsWith(derived.Output.Trim(), StringComparison.Ordinal),
            "The generated SSH private and public keys disagree.");
    }

    public async Task TrustAsync(
        SeedState state,
        string directory,
        string independentFingerprint,
        CancellationToken cancellationToken)
    {
        SeedGuard.Require(Regex.IsMatch(independentFingerprint, @"^SHA256:[A-Za-z0-9+/]{43}$"),
            "Confirm the exact ed25519 SSH host fingerprint independently from the provider console.");
        var knownHosts = Path.Combine(directory, "known_hosts");
        var existing = File.Exists(knownHosts) ? File.ReadAllText(knownHosts) : null;
        if (existing is not null)
        {
            SeedFiles.Check(knownHosts);
            SeedGuard.Require(Fingerprint(existing, state.Server!.Address!) == independentFingerprint,
                "The pinned SSH host identity changed. No command or secret was sent.");
            state.HostFingerprint = independentFingerprint;
            return;
        }
        var scan = await RequireAsync("ssh-keyscan",
            ["-T", "15", "-t", "ed25519", state.Server!.Address!], null, cancellationToken);
        var candidates = scan.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).Distinct(StringComparer.Ordinal).ToArray();
        SeedGuard.Require(candidates.Length == 1
            && Fingerprint(candidates[0], state.Server.Address!) == independentFingerprint,
            "The SSH candidate differs from the independently obtained provider-console fingerprint. Nothing was pinned.");
        SeedFiles.WriteNew(knownHosts, Encoding.UTF8.GetBytes(candidates[0] + "\n"));
        state.HostFingerprint = independentFingerprint;
    }

    public async Task PrepareAsync(
        SeedState state,
        string directory,
        CancellationToken cancellationToken)
    {
        const string script = """
set -eu
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl docker.io docker-compose-v2 openssl openssh-client python3
systemctl enable --now docker
install -d -m 0700 __REMOTE__
""";
        var result = await SshAsync(state, directory,
            script.Replace("__REMOTE__", RemoteDirectory(state), StringComparison.Ordinal),
            cancellationToken);
        SeedGuard.Require(result.ExitCode == 0,
            "Ubuntu control-plane prerequisites failed. The billable VPS and its pinned SSH identity are retained.");
    }

    public async Task<bool> DnsReadyAsync(SeedState state, CancellationToken cancellationToken)
    {
        var selection = state.Plan.Selection;
        foreach (var name in new[] { selection.DashboardHostname, selection.ApiHostname, "*." + selection.GatewayDomain })
        {
            var probe = name.StartsWith("*.", StringComparison.Ordinal)
                ? "workspaces-seed-probe." + name[2..]
                : name;
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(probe, cancellationToken);
                if (addresses.Length == 0 || addresses.Any(address => address.ToString() != state.Server!.Address))
                    return false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }
        return true;
    }

    public async Task InstallAsync(
        SeedState state,
        string directory,
        string releaseDirectory,
        CancellationToken cancellationToken)
    {
        var remote = RemoteDirectory(state);
        var remoteRelease = remote + "/release";
        await RequireSshAsync(state, directory,
            "install -d -m 0700 " + SeedGuard.SafeShellQuote(remoteRelease), cancellationToken);
        var names = new[]
        {
            "release-manifest.json",
            state.Plan.Release.BundleName,
            "checksums.txt",
            "checksums.txt.sigstore.json"
        };
        foreach (var name in names)
        {
            var local = Path.Combine(releaseDirectory, name);
            SeedFiles.Check(local);
            await UploadAsync(state, directory, local, remoteRelease + "/" + name, cancellationToken);
        }
        var selection = state.Plan.Selection;
        var setupToken = Path.Combine(directory, "setup-token");
        var archiveRoot = remoteRelease + "/extracted";
        var config = string.Join(" ",
            ["--release-tag", Quote(selection.ReleaseTag),
             "--installation-id", Quote(state.InstallationId!),
             "--dashboard-hostname", Quote(selection.DashboardHostname),
             "--api-hostname", Quote(selection.ApiHostname),
             "--gateway-domain", Quote(selection.GatewayDomain)]);
        var command = "cd " + Quote(remoteRelease)
            + " && sha256sum --check --strict --status checksums.txt"
            + " && if [ ! -f " + Quote(archiveRoot + "/.verified") + " ]; then "
            + "rm -rf " + Quote(archiveRoot) + " " + Quote(archiveRoot + ".pending")
            + " && install -d -m 700 " + Quote(archiveRoot + ".pending")
            + " && tar -xzf " + Quote(state.Plan.Release.BundleName) + " -C "
            + Quote(archiveRoot + ".pending") + " --no-same-owner"
            + " && pending_root=$(find " + Quote(archiveRoot + ".pending")
            + " -mindepth 1 -maxdepth 1 -type d -print -quit)"
            + " && [ -n \"$pending_root\" ]"
            + " && [ \"$(find " + Quote(archiveRoot + ".pending")
            + " -mindepth 1 -maxdepth 1 -type d | wc -l)\" -eq 1 ]"
            + " && python3 -B \"$pending_root/verify_bundle.py\""
            + " && touch \"$pending_root/../.verified\""
            + " && mv " + Quote(archiveRoot + ".pending") + " " + Quote(archiveRoot)
            + "; fi"
            + " && bundle_root=$(find " + Quote(archiveRoot) + " -mindepth 1 -maxdepth 1 -type d -print -quit)"
            + " && [ -n \"$bundle_root\" ] && [ \"$(find " + Quote(archiveRoot)
            + " -mindepth 1 -maxdepth 1 -type d | wc -l)\" -eq 1 ]"
            + " && [ -f " + Quote(archiveRoot + "/.verified") + " ]"
            + " && \"$bundle_root/seed-start.sh\" " + config
            + " --release-root \"$bundle_root\""
            + " --install-root /opt/municode/workspaces"
            + " --secrets-dir /etc/municode/workspaces/secrets"
            + " --setup-token-output " + Quote(remote + "/setup-token");
        await RequireSshAsync(state, directory, command, cancellationToken);
        await DownloadAsync(state, directory, remote + "/setup-token", setupToken, cancellationToken);
    }

    public async Task VerifyHttpsAndHealthAsync(SeedState state, CancellationToken cancellationToken)
    {
        var selection = state.Plan.Selection;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var url in new[]
                 {
                     "https://" + selection.ApiHostname + "/health",
                     "https://" + selection.ApiHostname + "/api/account/setup",
                     "https://" + selection.DashboardHostname + "/"
                 })
        {
            using var response = await client.GetAsync(url, cancellationToken);
            SeedGuard.Require(response.IsSuccessStatusCode,
                $"Public HTTPS verification failed for {url}; no dashboard/API fallback or unsafe certificate acceptance was used.");
        }
    }

    internal static string Fingerprint(string hostLine, string address)
    {
        var fields = hostLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        SeedGuard.Require(fields.Length == 3 && fields[0] == address && fields[1] == "ssh-ed25519",
            "The candidate SSH key is not one exact ed25519 host entry.");
        try
        {
            var key = Convert.FromBase64String(fields[2]);
            SeedGuard.Require(key.Length == 51
                && Encoding.ASCII.GetString(key, 4, 11) == "ssh-ed25519"
                && key[0] == 0 && key[1] == 0 && key[2] == 0 && key[3] == 11
                && key[15] == 0 && key[16] == 0 && key[17] == 0 && key[18] == 32,
                "The candidate SSH ed25519 wire key is invalid.");
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        }
        catch (FormatException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "The SSH host key encoding is invalid.");
        }
    }

    private IEnumerable<string> StrictOptions(string directory) =>
    [
        "-F", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=yes", "-o", "CheckHostIP=yes",
        "-o", "ConnectTimeout=20", "-o", "PasswordAuthentication=no",
        "-o", "KbdInteractiveAuthentication=no", "-o", "ForwardAgent=no",
        "-o", "ClearAllForwardings=yes", "-o", "HostKeyAlgorithms=ssh-ed25519",
        "-o", "UpdateHostKeys=no",
        "-o", "GlobalKnownHostsFile=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
        "-o", "UserKnownHostsFile=" + Path.Combine(directory, "known_hosts")
    ];

    private async Task<SeedProcessResult> SshAsync(
        SeedState state,
        string directory,
        string command,
        CancellationToken cancellationToken)
    {
        SeedGuard.Require(state.Server?.Address is not null && state.HostFingerprint is not null,
            "SSH commands require an attributed public IPv4 and pinned host fingerprint.");
        var args = StrictOptions(directory)
            .Concat(["-i", Path.Combine(directory, "id_ed25519"), "root@" + state.Server.Address])
            .Append("env -i HOME=/root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin "
                + command)
            .ToArray();
        return await _process.RunAsync("ssh", args, null, cancellationToken);
    }

    private async Task RequireSshAsync(
        SeedState state,
        string directory,
        string command,
        CancellationToken cancellationToken)
    {
        var result = await SshAsync(state, directory, command, cancellationToken);
        SeedGuard.Require(result.ExitCode == 0,
            "A pinned SSH host operation failed. The journal and billable VPS are retained; diagnostics may contain secrets and were suppressed.");
    }

    private async Task UploadAsync(
        SeedState state,
        string directory,
        string local,
        string remote,
        CancellationToken cancellationToken)
    {
        var args = StrictOptions(directory).Where(option => option != "-T")
            .Concat(["-i", Path.Combine(directory, "id_ed25519"), local,
                "root@" + state.Server!.Address + ":" + remote])
            .ToArray();
        var result = await _process.RunAsync("scp", args, null, cancellationToken);
        SeedGuard.Require(result.ExitCode == 0, "Uploading a release asset over pinned SSH failed.");
    }

    private async Task DownloadAsync(
        SeedState state,
        string directory,
        string remote,
        string local,
        CancellationToken cancellationToken)
    {
        var args = StrictOptions(directory).Where(option => option != "-T")
            .Concat(["-i", Path.Combine(directory, "id_ed25519"),
                "root@" + state.Server!.Address + ":" + remote, local + ".pending"])
            .ToArray();
        if (File.Exists(local + ".pending")) File.Delete(local + ".pending");
        var result = await _process.RunAsync("scp", args, null, cancellationToken);
        SeedGuard.Require(result.ExitCode == 0, "Downloading the first-owner setup token over pinned SSH failed.");
        SeedGuard.Require(File.Exists(local + ".pending"),
            "The first-owner token download did not create a protected local result.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(local + ".pending",
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        SeedFiles.Check(local + ".pending");
        var pending = File.ReadAllText(local + ".pending").Trim();
        SeedGuard.Require(Regex.IsMatch(pending, "^[0-9a-f]{64}$"),
            "The downloaded first-owner setup token is incomplete or malformed.");
        if (File.Exists(local))
        {
            SeedFiles.Check(local);
            SeedGuard.Require(File.ReadAllText(local).Trim() == pending,
                "The first-owner setup token changed across resume. Reconcile setup state; no token replacement was accepted.");
            File.Delete(local + ".pending");
            return;
        }
        File.Move(local + ".pending", local);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(local, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task<SeedProcessResult> RequireAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? input,
        CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(fileName, arguments, input, cancellationToken);
        SeedGuard.Require(result.ExitCode == 0,
            $"Required seed tool '{fileName}' is unavailable or failed. No provider mutation was attempted.");
        return result;
    }

    private static string Quote(string value) => SeedGuard.SafeShellQuote(value);
}
