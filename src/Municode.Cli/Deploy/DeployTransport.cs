// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Instances;
using Municode.Cli.Seed;
using static Municode.Cli.Instances.InstanceSsh;

namespace Municode.Cli.Deploy;

internal sealed record DeployUnit(string ActiveState, string SubState)
{
    internal bool IsBusy => ActiveState is not ("inactive" or "failed" or "unknown");
}

internal sealed record DeployHostOperation(
    string OperationId, string Phase, string Source, string? PendingStep, DeployUnit? Unit,
    bool TrafficMayHaveOpened, string? OpenAt, bool WorkerResumed, bool BackupCreated);

internal sealed record DeployHostResult(DeployHostOperation? State, string[] Blockers, string? ApprovalDigest);

internal sealed class DeployTransport(InstanceRegistry registry, InstanceEntry instance)
{
    private const string FindLatest = """
        import re, stat, sys
        from pathlib import Path

        root = Path('/var/lib/municode/deploys')
        if not root.is_dir() or root.resolve() != root:
            sys.exit(1)
        paths = []
        for directory in root.iterdir():
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
                paths.append((info.st_mtime_ns, directory.name))
        if paths:
            print(max(paths)[1])
        """;

    public async Task StageAsync(DeployPlan plan, DeployRelease release, CancellationToken ct)
    {
        var created = await registry.RunPinnedAsync(instance, "umask 077; mktemp -d /var/tmp/municode-deploy.XXXXXXXXXX", ct);
        var directory = created.Output.Trim();
        SeedGuard.Require(created.ExitCode == 0 && Regex.IsMatch(directory, @"\A/var/tmp/municode-deploy\.[A-Za-z0-9]{10}\z"),
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

    public async Task<DeployHostOperation?> LatestAsync(CancellationToken ct)
    {
        var result = await registry.RunPinnedAsync(instance, AsRoot(
            $"exec python3 -I -c {Quote(FindLatest)}"), ct);
        SeedGuard.Require(result.ExitCode == 0, "Could not discover the latest host deploy operation over pinned SSH.");
        var operation = result.Output.Trim();
        return operation.Length == 0 ? null : await StatusAsync(operation, ct);
    }

    public async Task<DeployHostOperation> StatusAsync(
        string operation, CancellationToken ct, DeployReleaseIdentity? target = null)
    {
        SeedGuard.Require(Regex.IsMatch(operation, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z"),
            "The host returned an invalid deploy operation ID.");
        var arguments = $"status --operation {Quote(operation)}";
        var active = Agent(instance.Host.Release.ReleaseId);
        // The active agent is a fallback for historical operations whose target release has been pruned.
        var command = (target is not null && target.ReleaseId != instance.Host.Release.ReleaseId
            ? $"if [ -f {AgentPath(target.ReleaseId)} ]; then exec {Agent(target.ReleaseId)} {arguments}; fi; " : "")
            + $"exec {active} {arguments}";
        var result = await registry.RunPinnedAsync(instance, AsRoot(command), ct);
        using var response = ParseAgentResult(result, "status");
        var root = response.RootElement;
        SeedGuard.Require(result.ExitCode == 0 && ReadBlockers(root).Length == 0,
            "Host deploy status failed: " + string.Join("; ", ReadBlockers(root)));
        var unit = root.GetProperty("unit");
        return ReadHostState(root.GetProperty("state"), operation, target,
            new DeployUnit(ReadToken(unit, "activeState"), ReadToken(unit, "subState")));
    }

    public async Task<DeployHostResult> ExecuteAsync(
        DeployOperation operation, DeployExecution execution, Stream? key, CancellationToken ct)
    {
        var op = operation.Plan.OperationId;
        var arguments = execution.Action switch
        {
            "apply" => $"run --approve {Quote(operation.ApprovalDigest!)}"
                + (execution.DrainTimeout is { } timeout ? FormattableString.Invariant($" --drain-timeout {timeout}") : ""),
            "rollback" => "rollback" + (execution.RestoreDatabase ? " --restore-database" : "")
                + (execution.AcceptDataLossSince is { } since ? $" --accept-data-loss-since {Quote(since)}" : "")
                + (execution.AcceptProviderReconciliation ? " --accept-provider-reconciliation" : ""),
            _ => throw new InvalidDataException("Invalid deploy execution action.")
        };
        var directory = $"/var/lib/municode/deploys/{op}";
        var planPath = directory + "/cli-plan.json";
        var command = "umask 077; "
            + $"[ \"$(realpath -e -- {Quote(directory)})\" = {Quote(directory)} ] || exit 1; "
            + $"plan=$(mktemp -- {Quote(directory + "/.cli-plan.XXXXXXXXXX")}) || exit 1; "
            + """trap 'rm -f -- "$plan"' EXIT; """
            + $"printf '%s' {Quote(JsonSerializer.Serialize(operation.Plan, SeedJournal.Json))} > \"$plan\""
            + $" && mv -T -- \"$plan\" {Quote(planPath)} || exit 1; "
            + $"exec systemd-run --unit municode-deploy-{op} --collect --pipe "
            + $"{Agent(operation.Plan.TargetRelease.ReleaseId)} {arguments} --plan {Quote(planPath)}"
            + (key is null ? "" : " --unseal-key-stdin");
        var result = await registry.RunPinnedAsync(instance, AsRoot(command), ct, key);
        SeedGuard.Require(result.ExitCode is 0 or 1 && !string.IsNullOrWhiteSpace(result.Output),
            $"No result received for deploy {op}. Run 'municode deploy resume {op}' to check the host operation.");
        using var response = ParseAgentResult(result, execution.Action);
        var root = response.RootElement;
        var blockers = ReadBlockers(root);
        var state = root.TryGetProperty("state", out var value)
            ? ReadHostState(value, op, operation.Plan.TargetRelease) : null;
        var digest = root.TryGetProperty("approvalDigest", out value) ? value.GetString() : null;
        SeedGuard.Require((result.ExitCode == 0 && state is not null && blockers.Length == 0)
            || (result.ExitCode == 1 && blockers.Length > 0),
            "The host returned an inconsistent deploy execution result.");
        SeedGuard.Require(digest is null || DeployApproval.IsDigest(digest), "The host returned an invalid approval digest.");
        return new(state, blockers, digest);
    }

    internal Task<SeedProcessResult> ReadBackupFileAsync(string operation, string file, CancellationToken ct, Stream? output = null)
    {
        var path = $"/var/lib/municode/backups/{operation}/{file}";
        return registry.RunPinnedAsync(instance, AsRoot(
            $"[ -f {Quote(path)} ] && [ \"$(realpath -e -- {Quote(path)})\" = {Quote(path)} ]"
            + $" && exec cat -- {Quote(path)}"), ct, output: output);
    }

    private static DeployHostOperation ReadHostState(
        JsonElement state, string operation, DeployReleaseIdentity? target, DeployUnit? unit = null)
    {
        var phase = ReadToken(state, "phase");
        SeedGuard.Require(!state.TryGetProperty("operationId", out var id) || id.GetString() == operation,
            "The host returned a different deploy operation ID.");
        SeedGuard.Require(target is null || phase == "staging"
            || state.GetProperty("targetRelease").Deserialize<DeployReleaseIdentity>(SeedJournal.Json) == target,
            "The host deploy operation no longer matches the staged release.");
        var step = OptionalString(state, "pendingStep");
        SeedGuard.Require(step is null || Regex.IsMatch(step, @"\A[A-Za-z0-9][A-Za-z0-9_.:/@-]{0,511}\z"),
            "The host returned an invalid deploy step.");
        var openAt = OptionalString(state, "openAt");
        SeedGuard.Require(openAt is null || IsTimestamp(openAt), "The host returned an invalid deploy open timestamp.");
        return new(operation, phase, "host-agent", step, unit, Flag("trafficMayHaveOpened"), openAt,
            Flag("workerResumed"), OptionalString(state, "backupCreatedAt") is not null);

        bool Flag(string name) => state.TryGetProperty(name, out var value) && value.GetBoolean();
    }

    private Task<SeedProcessResult> RunAgentAsync(string releaseId, string arguments, CancellationToken ct) =>
        registry.RunPinnedAsync(instance, AsRoot($"exec {Agent(releaseId)} {arguments}"), ct);

    private static string Agent(string releaseId) => $"python3 -E -s -B {AgentPath(releaseId)}";

    private static string AgentPath(string releaseId)
    {
        SeedGuard.Require(Regex.IsMatch(releaseId, @"\A(?:municode-v[0-9][A-Za-z0-9.-]*|local-[a-f0-9]{12}-[a-f0-9]{12})\z"),
            "Invalid deploy release ID.");
        return Quote($"/opt/municode/releases/{releaseId}/deploy_host.py");
    }

    internal static bool IsTimestamp(string value) =>
        Regex.IsMatch(value, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z")
        && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static string ReadToken(JsonElement value, string name)
    {
        var token = value.GetProperty(name).GetString();
        SeedGuard.Require(token is not null && Regex.IsMatch(token, @"\A[A-Za-z][A-Za-z0-9_-]{0,79}\z"),
            "The host returned invalid deploy operation metadata.");
        return token!;
    }

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
