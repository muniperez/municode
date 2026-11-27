// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Instances;

internal sealed record InstanceRelease(
    string ReleaseId, string? Tag, string SourceRevision, string BundleSha256, string Trust,
    DateTimeOffset ActivatedAt);

internal sealed record InstanceHost(
    string InstallationId, string DashboardHostname, string ApiHostname, string GatewayDomain,
    InstanceRelease Release);

internal sealed record InstancePin(string Line, string Fingerprint)
{
    public const string Alias = "municode-instance";

    public static bool IsFingerprint(string? value) =>
        value is not null && Regex.IsMatch(value, @"\ASHA256:[A-Za-z0-9+/]{43}\z");

    public static InstancePin Parse(string text, string expectedHost)
    {
        var fields = text.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        SeedGuard.Require(fields.Length == 3 && fields[0] == expectedHost
            && Regex.IsMatch(fields[1], @"\A[A-Za-z0-9@._+-]+\z")
            && Regex.IsMatch(fields[2], @"\A[A-Za-z0-9+/]+={0,2}\z"),
            "Expected exactly one pinned SSH host key.");
        Span<byte> key = stackalloc byte[8192];
        SeedGuard.Require(Convert.TryFromBase64String(fields[2], key, out var length) && length > 0,
            "The pinned SSH host key is invalid.");
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key[..length])).TrimEnd('=');
        return new($"{Alias} {fields[1]} {fields[2]}\n", fingerprint);
    }
}

internal sealed class InstanceSsh(ISeedProcess process)
{
    private const string Probe = """
        import json
        import sys

        try:
            with open('/var/lib/municode/active-release.json', encoding='utf-8') as stream:
                release = json.load(stream)
        except FileNotFoundError:
            sys.exit(41)
        except (OSError, ValueError):
            sys.exit(42)

        keys = {
            'MUNICODE_INSTALLATION_ID': 'installationId',
            'CONSOLE_HOSTNAME': 'dashboardHostname',
            'API_HOSTNAME': 'apiHostname',
            'MUNICODE_GATEWAY_DOMAIN': 'gatewayDomain',
        }
        try:
            with open('/etc/municode/config/operator.env', encoding='utf-8') as stream:
                host = {}
                for line in stream:
                    key, sep, value = line.strip().partition('=')
                    if sep and key.strip() in keys:
                        field = keys[key.strip()]
                        if field in host:
                            sys.exit(42)
                        value = value.strip()
                        if len(value) >= 2 and value[0] in ('"', "'") and value[-1] == value[0]:
                            value = value[1:-1]
                        host[field] = value
            host['release'] = {key: release[key] for key in
                ('releaseId', 'tag', 'sourceRevision', 'bundleSha256', 'trust', 'activatedAt')}
        except (OSError, ValueError, TypeError, KeyError):
            sys.exit(42)
        print(json.dumps(host))
        """;

    public static void ValidateDestination(string destination) => ParseDestination(destination);

    public async Task<InstancePin> DiscoverAsync(string destination, string knownHosts, CancellationToken ct)
    {
        // Let OpenSSH resolve aliases/proxies and negotiate a key, without authenticating
        // to the target or using the operator's usual known_hosts.
        await process.RunAsync("ssh",
            [.. Options(knownHosts, "accept-new"),
                "-o", "PreferredAuthentications=none", "-o", "PubkeyAuthentication=no",
                "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
                "-o", "HostbasedAuthentication=no", "-o", "GSSAPIAuthentication=no",
                .. DestinationArguments(destination), "exit"], null, ct);
        SeedGuard.Require(new FileInfo(knownHosts).Length > 0,
            "Could not discover an SSH host key. Check that the SSH destination is reachable.");
        SeedFiles.Check(knownHosts);
        return InstancePin.Parse(File.ReadAllText(knownHosts), InstancePin.Alias);
    }

    public async Task<InstanceHost> ProbeAsync(InstanceConnection entry, string knownHosts, CancellationToken ct)
    {
        var command = "python3 -I -c " + Quote(Probe);
        var result = await RunPinnedAsync(entry, knownHosts,
            $"if [ \"$(id -u)\" = 0 ]; then exec {command}; fi; "
            + $"sudo -n -- /bin/sh -c {Quote(command)}; "
            + "case $? in 0) exit 0;; 41) exit 41;; 42) exit 42;; *) exit 43;; esac", ct);
        SeedGuard.Require(result.ExitCode != 41,
            "The host has no active-release.json; reinstall with install.sh from a release that supports deploy.");
        SeedGuard.Require(result.ExitCode != 43,
            "The SSH user needs root access or passwordless sudo (sudo -n).");
        SeedGuard.Require(result.ExitCode != 42, "The host has invalid deploy release or operator configuration.");
        SeedGuard.Require(result.ExitCode == 0,
            "The pinned SSH probe failed. Check connectivity, authentication and the pinned host key.");

        try
        {
            var host = JsonSerializer.Deserialize<InstanceHost>(result.Output, SeedJournal.Json);
            SeedGuard.Require(host is not null && IsToken(host.InstallationId)
                && IsHostname(host.DashboardHostname) && IsHostname(host.ApiHostname) && IsHostname(host.GatewayDomain)
                && host.Release is { } release && IsToken(release.ReleaseId)
                && (release.Tag is null || IsToken(release.Tag))
                && IsHex(release.SourceRevision, 40) && IsHex(release.BundleSha256, 64)
                && IsToken(release.Trust) && release.ActivatedAt != default,
                "The host returned incomplete or invalid deploy metadata.");
            return host!;
        }
        catch (JsonException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "The host returned invalid deploy metadata.");
        }
    }

    private Task<SeedProcessResult> RunPinnedAsync(
        InstanceConnection entry, string knownHosts, string command, CancellationToken ct)
    {
        SeedFiles.Check(knownHosts);
        SeedGuard.Require(InstancePin.Parse(File.ReadAllText(knownHosts), InstancePin.Alias).Fingerprint
            == entry.HostFingerprint, "The registry's pinned host key does not match the recorded fingerprint.");
        var args = new List<string>(Options(knownHosts, "yes"));
        if (entry.SeedOperationId is not null)
        {
            SeedFiles.Check(entry.IdentityFile!);
            args.AddRange(["-F", "none", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none"]);
        }
        if (entry.IdentityFile is not null) args.AddRange(["-i", entry.IdentityFile]);
        args.AddRange(DestinationArguments(entry.Destination));
        args.Add(command);
        return process.RunAsync("ssh", args, null, ct);
    }

    private static string[] Options(string knownHosts, string checking)
    {
        SeedGuard.Require(!knownHosts.Contains("${", StringComparison.Ordinal) && !knownHosts.Any(char.IsControl),
            "The registry path cannot contain SSH environment expansions or control characters.");
        var path = knownHosts.Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return
        [
            "-n", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20",
            "-o", "StrictHostKeyChecking=" + checking, "-o", "HostKeyAlias=" + InstancePin.Alias,
            "-o", $"UserKnownHostsFile=\"{path}\"", "-o", "GlobalKnownHostsFile=none",
            "-o", "KnownHostsCommand=none", "-o", "VerifyHostKeyDNS=no", "-o", "CheckHostIP=no",
            "-o", "HashKnownHosts=no", "-o", "UpdateHostKeys=no", "-o", "ControlPath=none",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "ClearAllForwardings=yes",
            "-o", "GSSAPIDelegateCredentials=no", "-o", "ForkAfterAuthentication=no",
            "-o", "Tunnel=no", "-o", "PermitLocalCommand=no", "-o", "RemoteCommand=none",
            "-o", "SessionType=default"
        ];
    }

    private static string[] DestinationArguments(string destination)
    {
        var (host, port) = ParseDestination(destination);
        return port is null ? ["--", host] : ["-p", port, "--", host];
    }

    private static (string Host, string? Port) ParseDestination(string destination)
    {
        var match = Regex.Match(destination,
            @"\A(?:ssh://)?(?<user>[a-zA-Z0-9_][a-zA-Z0-9_.-]*@)?"
            + @"(?<host>[a-zA-Z0-9_][a-zA-Z0-9_.-]*|\[[0-9a-fA-F:.]+\])(?::(?<port>[0-9]{1,5}))?\z");
        if (match.Success)
        {
            var port = match.Groups["port"];
            var host = match.Groups["host"].Value.Trim('[', ']');
            SeedGuard.Require(!port.Success || int.Parse(port.Value, System.Globalization.CultureInfo.InvariantCulture)
                is > 0 and <= 65535, "The SSH port must be between 1 and 65535.");
            return (match.Groups["user"].Value + host, port.Success ? port.Value : null);
        }
        var ipv6 = Regex.Match(destination,
            @"\A(?<user>[a-zA-Z0-9_][a-zA-Z0-9_.-]*@)?(?<host>[a-fA-F0-9:.]+)\z");
        SeedGuard.Require(ipv6.Success && IPAddress.TryParse(ipv6.Groups["host"].Value, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
            "Use an SSH host or config alias, user@host, or host:port (bracket IPv6 addresses with a port).");
        return (destination, null);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool IsToken(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._+-]{0,252}\z");

    private static bool IsHostname(string? value) => value is not null && IsToken(value)
        && Uri.CheckHostName(value) == UriHostNameType.Dns;

    private static bool IsHex(string? value, int length) =>
        value?.Length == length && value.All(Uri.IsHexDigit);
}
