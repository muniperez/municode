// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Instances;
using Municode.Cli.Seed;
using static Municode.Cli.Instances.InstanceSsh;

namespace Municode.Cli.Deploy;

internal sealed record DeployHostOperation(string OperationId, string Phase, string Source);

internal sealed class DeployTransport(InstanceRegistry registry, InstanceEntry instance)
{
    private const string ReadState = """
        import json, os, re, stat, sys
        from pathlib import Path

        root = Path('/var/lib/municode/deploys')
        if not root.is_dir() or root.resolve() != root:
            sys.exit(1)
        paths = []
        operation = sys.argv[1]
        for directory in ([root / operation] if operation else root.iterdir()):
            if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,79}', directory.name):
                continue
            if directory.is_symlink() or not directory.is_dir():
                continue
            path = directory / 'state.json'
            try:
                info = path.lstat()
            except FileNotFoundError:
                continue
            if stat.S_ISREG(info.st_mode):
                paths.append((info.st_mtime_ns, directory.name, path))
        if not paths:
            print('null')
        else:
            _, name, path = max(paths)
            with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as stream:
                info = os.fstat(stream.fileno())
                if not stat.S_ISREG(info.st_mode) or info.st_size > 1024 * 1024:
                    sys.exit(1)
                state = json.load(stream)
            phase = state['phase']
            if not isinstance(phase, str) or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_-]{0,79}', phase):
                sys.exit(1)
            print(json.dumps(dict(operationId=name, phase=phase, source='state-file')))
        """;

    public async Task StageAsync(DeployPlan plan, DeployRelease release, CancellationToken ct)
    {
        var created = await registry.RunPinnedAsync(instance, "umask 077; mktemp -d /tmp/municode-deploy.XXXXXXXXXX", ct);
        var directory = created.Output.Trim();
        SeedGuard.Require(created.ExitCode == 0 && Regex.IsMatch(directory, @"\A/tmp/municode-deploy\.[A-Za-z0-9]{10}\z"),
            "Could not create an SSH-user-owned deploy upload directory.");
        var path = directory + "/bundle.tar.gz";
        try
        {
            await using var bundle = File.OpenRead(release.ArchivePath);
            var uploaded = await registry.RunPinnedAsync(instance, $"umask 077; cat > {Quote(path)}", ct, bundle);
            SeedGuard.Require(uploaded.ExitCode == 0, "The release upload over pinned SSH failed.");
            var tag = release.Identity.Trust == DeployReleaseSource.LocalBuild ? ""
                : " --verified-release-tag " + Quote(release.Identity.Tag);
            var result = await RunAgentAsync(instance.Host.Release.ReleaseId,
                $"stage --json --operation {Quote(plan.OperationId)} --bundle {Quote(path)}"
                + $" --bundle-sha256 {Quote(release.Identity.BundleSha256)} --trust {Quote(release.Identity.Trust)}{tag}", ct);
            using var response = ParseAgentResult(result, "stage");
            SeedGuard.Require(result.ExitCode == 0, "Host deploy staging failed: "
                + string.Join("; ", ReadBlockers(response.RootElement)));
            SeedGuard.Require(response.RootElement.GetProperty("deployProtocol").GetInt32() == plan.DeployProtocol,
                "Host deploy protocol mismatch; use the CLI from the target release (cli/<rid>/).");
            SeedGuard.Require(response.RootElement.GetProperty("targetRelease")
                .Deserialize<DeployReleaseIdentity>(SeedJournal.Json) == release.Identity,
                "The host staged release identity does not match the verified bundle.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var removed = await registry.RunPinnedAsync(instance,
                $"rm -f -- {Quote(path)} && rmdir -- {Quote(directory)}", cleanup.Token);
            SeedGuard.Require(removed.ExitCode == 0, "Could not remove the SSH deploy upload directory.");
        }
    }

    public async Task<(JsonElement Result, string[] Blockers)> PreflightAsync(DeployPlan plan, CancellationToken ct)
    {
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(plan, SeedJournal.Json));
        var agent = Agent(plan.TargetRelease.ReleaseId);
        var result = await registry.RunPinnedAsync(instance, AsRoot(
            "umask 077; plan=$(mktemp /tmp/municode-plan.XXXXXXXXXX) || exit 1; "
            + """trap 'rm -f -- "$plan"' EXIT; cat > "$plan" || exit 1; """
            + $" {agent} preflight --json --plan \"$plan\""), ct, input);
        using var response = ParseAgentResult(result, "preflight");
        var blockers = ReadBlockers(response.RootElement);
        SeedGuard.Require((result.ExitCode == 0 && blockers.Length == 0)
            || (result.ExitCode == 1 && blockers.Length > 0),
            "The host returned an inconsistent deploy preflight result.");
        return (response.RootElement.Clone(), blockers);
    }

    public async Task<DeployHostOperation?> StatusAsync(string? operation, CancellationToken ct)
    {
        var result = await registry.RunPinnedAsync(instance, AsRoot(
            $"exec python3 -I -c {Quote(ReadState)} {Quote(operation ?? "")}"), ct);
        SeedGuard.Require(result.ExitCode == 0, "Could not read the host's deploy operation state over pinned SSH.");
        var state = JsonSerializer.Deserialize<DeployHostOperation>(result.Output, SeedJournal.Json);
        SeedGuard.Require(state is null || (state.OperationId is not null
            && Regex.IsMatch(state.OperationId, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z")
            && (operation is null || state.OperationId == operation)
            && state.Phase is not null && Regex.IsMatch(state.Phase, @"\A[A-Za-z][A-Za-z0-9_-]{0,79}\z")
            && state.Source == "state-file"), "The host returned invalid deploy operation metadata.");
        return state;
    }

    private Task<SeedProcessResult> RunAgentAsync(string releaseId, string arguments, CancellationToken ct) =>
        registry.RunPinnedAsync(instance, AsRoot($"exec {Agent(releaseId)} {arguments}"), ct);

    private static string Agent(string releaseId) =>
        $"python3 -E -s -B {Quote($"/opt/municode/releases/{releaseId}/deploy_host.py")}";

    private static JsonDocument ParseAgentResult(SeedProcessResult result, string action)
    {
        if (result.ExitCode is not (0 or 1) || string.IsNullOrWhiteSpace(result.Output))
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"Host deploy {action} failed over pinned SSH. Check connectivity, sudo -n and the installed deploy agent.");
        return JsonDocument.Parse(result.Output);
    }

    private static string[] ReadBlockers(JsonElement response)
    {
        if (!response.TryGetProperty("blockers", out var blockers)) return [];
        return blockers.EnumerateArray().Select(item => item.GetString() ?? "Unspecified host blocker.")
            .Select(message => new string(message.Where(ch => !char.IsControl(ch)).Take(512).ToArray())).ToArray();
    }
}
