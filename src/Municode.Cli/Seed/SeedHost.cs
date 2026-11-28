// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Deploy;

namespace Municode.Cli.Seed;

internal sealed class SeedHost(ISeedProcess? runner = null) : ISeedHost
{
    private readonly ISeedProcess _process = runner ?? SeedProcess.Default;

    internal static string RemoteDirectory(SeedState state) => "/var/lib/municode-seed/" + state.Id;

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
                ["-q", "-t", "ed25519", "-N", "", "-C", "municode-seed", "-f", key],
                null, cancellationToken);
        }
        SeedFiles.Check(key);
        SeedFiles.CheckPublicKey(key + ".pub");
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
if [ "__SMALL_VULTR__" = yes ]; then
    swap_file=/var/lib/municode-seed.swap
    if [ ! -f "$swap_file" ] && ! swapon --noheadings --show=NAME | grep -q .; then
        temporary_swap=$(mktemp "$swap_file.XXXXXX")
        trap 'rm -f "$temporary_swap"' EXIT
        fallocate -l 1G "$temporary_swap"
        mkswap "$temporary_swap"
        mv "$temporary_swap" "$swap_file"
        trap - EXIT
    fi
    if [ -f "$swap_file" ]; then
        if ! swapon --noheadings --show=NAME | grep -Fxq "$swap_file"; then
            swapon "$swap_file"
        fi
        if ! grep -Fq "$swap_file " /etc/fstab; then
            printf '%s none swap sw 0 0\n' "$swap_file" >> /etc/fstab
        fi
    fi
fi
apt-get update -qq
compose_package=docker-compose-v2
if [ "__SMALL_VULTR__" = yes ]; then
    compose_package=docker-compose
fi
apt-get install -y -qq ca-certificates curl docker.io "$compose_package" openssl openssh-client python3
systemctl enable --now docker
install -d -m 0700 __REMOTE__
""";
        var result = await SshAsync(state, directory,
            script.Replace("__REMOTE__", RemoteDirectory(state), StringComparison.Ordinal)
                .Replace("__SMALL_VULTR__", state.Plan.Selection is { Provider: "vultr", Size: "vc2-1c-0.5gb" } ? "yes" : "no",
                    StringComparison.Ordinal),
            cancellationToken);
        SeedGuard.Require(result.ExitCode == 0,
            "Control-plane prerequisites failed. The billable VPS and its pinned SSH identity are retained.");
    }

    public async Task<bool> DnsReadyAsync(SeedState state, CancellationToken cancellationToken)
    {
        var selection = state.Plan.Selection;
        foreach (var name in new[] { selection.DashboardHostname, selection.ApiHostname, "*." + selection.GatewayDomain })
        {
            var probe = name.StartsWith("*.", StringComparison.Ordinal)
                ? "municode-seed-probe." + name[2..]
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
        var localBuild = state.Plan.Release.Trust == DeployReleaseSource.LocalBuild;
        string[] names = localBuild
            ? ["release-manifest.json", state.Plan.Release.BundleName, "checksums.txt"]
            : ["release-manifest.json", state.Plan.Release.BundleName, "checksums.txt", "checksums.txt.sigstore.json"];
        foreach (var name in names)
        {
            var local = Path.Combine(releaseDirectory, name);
            // A local build stays in its build output directory; its contents were re-verified against the plan.
            if (localBuild)
                SeedRelease.CheckLocalAsset(local, name.EndsWith(".tar.gz", StringComparison.Ordinal)
                    ? 32L * 1024 * 1024 * 1024 : 128L * 1024 * 1024);
            else SeedFiles.Check(local);
            await UploadAsync(state, directory, local, remoteRelease + "/" + name, cancellationToken);
        }
        var selection = state.Plan.Selection;
        var setupToken = Path.Combine(directory, "setup-token");
        // Bundles older than #208 lack --workspace-defaults; only pass it when seed-start advertises it.
        var defaultsFile = remote + "/workspace-defaults.json";
        var defaultsArgs = "";
        if (state.RegisterWorkspaceProvider && state.Plan.Quote.Workspace is { } workspace)
        {
            var policy = JsonSerializer.SerializeToUtf8Bytes(workspace.Policy(selection.Region));
            var written = await SshAsync(state, directory,
                "umask 077 && cat > " + Quote(defaultsFile), cancellationToken, policy);
            SeedGuard.Require(written.ExitCode == 0, "Uploading the default workspace policy over pinned SSH failed.");
            defaultsArgs = " && if grep -qF -e '--workspace-defaults' \"$bundle_root/seed-start.sh\"; then defaults_args="
                + Quote("--workspace-defaults " + defaultsFile) + "; fi";
        }
        var archiveRoot = remoteRelease + "/extracted";
        // Bundles older than #208 lack --management-address; only pass it when seed-start advertises it.
        var managementAddress = ManagementAddress(state);
        var config = string.Join(" ",
            ["--release-tag", Quote(selection.ReleaseTag),
             "--installation-id", Quote(state.InstallationId!),
             "--dashboard-hostname", Quote(selection.DashboardHostname),
             "--api-hostname", Quote(selection.ApiHostname),
             "--gateway-domain", Quote(selection.GatewayDomain),
             .. (localBuild ? new[] { "--trust", DeployReleaseSource.LocalBuild } : [])]);
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
            + " && printf '%s' " + Quote(state.Plan.Release.BundleSha256)
            + " > \"$pending_root/../.verified\""
            + " && mv " + Quote(archiveRoot + ".pending") + " " + Quote(archiveRoot)
            + "; fi"
            + " && bundle_root=$(find " + Quote(archiveRoot) + " -mindepth 1 -maxdepth 1 -type d -print -quit)"
            + " && [ -n \"$bundle_root\" ] && [ \"$(find " + Quote(archiveRoot)
            + " -mindepth 1 -maxdepth 1 -type d | wc -l)\" -eq 1 ]"
            + " && [ -f " + Quote(archiveRoot + "/.verified") + " ]"
            + " && [ \"$(cat " + Quote(archiveRoot + "/.verified") + ")\" = "
            + Quote(state.Plan.Release.BundleSha256) + " ]"
            + " && python3 -B \"$bundle_root/verify_bundle.py\""
            + " && management_args=''"
            + " && if grep -qF -e '--management-address' \"$bundle_root/seed-start.sh\"; then management_args="
            + Quote("--management-address " + managementAddress) + "; fi"
            + " && defaults_args=''" + defaultsArgs
            + " && \"$bundle_root/seed-start.sh\" " + config + " $management_args $defaults_args"
            + " --release-root \"$bundle_root\""
            + " --bundle-sha256 " + Quote(state.Plan.Release.BundleSha256)
            + " --setup-token-output " + Quote(remote + "/setup-token");
        await RequireSshAsync(state, directory, command, cancellationToken);
        await DownloadAsync(state, directory, remote + "/setup-token", setupToken, cancellationToken);
    }

    public async Task<bool> StageWorkspaceProviderAsync(
        SeedState state,
        string directory,
        string credential,
        CancellationToken cancellationToken)
    {
        // The credential travels only on pinned SSH stdin into the maintenance container; never argv or logs.
        var values = new Dictionary<string, object>
        {
            ["providerType"] = state.Plan.Selection.Provider,
            ["credential"] = credential
        };
        if (state.Plan.Quote.Workspace is { } workspace)
            values["workspaceDefaults"] = new Dictionary<string, string>
            {
                ["providerAccountId"] = SeedWorkspaceDefaults.ProviderAccountId,
                ["planId"] = SeedWorkspaceDefaults.PlanId,
                ["region"] = state.Plan.Selection.Region,
                ["sku"] = workspace.Sku
            };
        var input = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var result = await SshAsync(state, directory,
                "/usr/local/sbin/municode-compose --profile maintenance run --rm --no-TTY api-bootstrap "
                + "stage-provider --stdin >/dev/null 2>&1", cancellationToken, input);
            return result.ExitCode == 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    internal static string ManagementAddress(SeedState state)
    {
        SeedGuard.Require(IPAddress.TryParse(state.Server?.Address, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any),
            "The control-plane VPS needs one attributed public IPv4 address.");
        return address + "/32";
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

    internal async Task<SeedProcessResult> SshAsync(
        SeedState state,
        string directory,
        string command,
        CancellationToken cancellationToken,
        byte[]? input = null)
    {
        SeedGuard.Require(state.Server?.Address is not null && state.HostFingerprint is not null,
            "SSH commands require an attributed public IPv4 and pinned host fingerprint.");
        var args = StrictOptions(directory)
            .Concat(["-i", Path.Combine(directory, "id_ed25519"), "root@" + state.Server.Address])
            .Append("env -i HOME=/root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin /bin/sh -c "
                + Quote(command))
            .ToArray();
        return await _process.RunAsync("ssh", args, input, cancellationToken);
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
        var expectedResult = await SshAsync(state, directory,
            "sha256sum " + Quote(remote) + " | cut -d ' ' -f 1", cancellationToken);
        SeedGuard.Require(expectedResult.ExitCode == 0
            && Regex.IsMatch(expectedResult.Output.Trim(), "^[0-9a-f]{64}$"),
            "The remote first-owner token hash could not be established over pinned SSH.");
        var expectedHash = expectedResult.Output.Trim();
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
        var pendingBytes = File.ReadAllBytes(local + ".pending");
        ValidateSetupTokenBytes(pendingBytes);
        SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(
                pendingBytes)) == expectedHash,
            "The downloaded first-owner setup token hash differs from the pinned remote value.");
        if (File.Exists(local))
        {
            SeedFiles.Check(local);
            var existingBytes = File.ReadAllBytes(local);
            ValidateSetupTokenBytes(existingBytes);
            SeedGuard.Require(CryptographicOperations.FixedTimeEquals(existingBytes, pendingBytes),
                "The first-owner setup token changed across resume. Reconcile setup state; no token replacement was accepted.");
            File.Delete(local + ".pending");
            return;
        }
        File.Move(local + ".pending", local);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(local, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ValidateSetupTokenBytes(byte[] value)
    {
        SeedGuard.Require(value.Length == 65 && value[^1] == (byte)'\n'
            && value.AsSpan(0, 64).ToArray().All(character =>
                character is >= (byte)'0' and <= (byte)'9'
                    or >= (byte)'a' and <= (byte)'f'),
            "The downloaded first-owner setup token is not exactly 64 lowercase hexadecimal bytes followed by one LF.");
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
