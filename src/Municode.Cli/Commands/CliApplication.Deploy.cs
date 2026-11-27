// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Deploy;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private const string DeployPlanUsage = "municode deploy plan --instance <name> "
        + "(--release <tag> | --release latest [--prerelease] | --bundle-dir <dir> --trust local-build) [--json]";
    private const string DeployStatusUsage = "municode deploy status (<operation> | --instance <name>) [--json]";

    private sealed record PublicDeployVersion(string Tag, string SourceRevision, string Trust);

    private void PrintDeployHelp()
    {
        _console.WriteLine("Usage:");
        _console.WriteLine($"  {DeployPlanUsage}");
        _console.WriteLine($"  {DeployStatusUsage}");
        _console.WriteLine("Plan verifies, stages and preflights a release without switching services.");
        _console.WriteLine("Status reads the active release, public version and selected or latest host operation.");
    }

    private async Task<int> RunDeployAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        SeedGuard.Require(action is "plan" or "status", "Usage: municode deploy <plan|status> [options].");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? operation = null;
        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (action == "status" && !option.StartsWith('-') && operation is null)
            {
                operation = option;
                continue;
            }
            if (option == "--json" || (action == "plan" && option == "--prerelease"))
                SeedGuard.Require(options.TryAdd(option, ""), $"Duplicate deploy option {option}.");
            else
            {
                SeedGuard.Require(option == "--instance" || (action == "plan" && option is "--release" or "--bundle-dir" or "--trust"),
                    "Unknown deploy option. See 'municode help deploy'.");
                SeedGuard.Require(index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])
                    && !args[index + 1].StartsWith("--", StringComparison.Ordinal), $"{option} requires a value.");
                SeedGuard.Require(options.TryAdd(option, args[++index]), $"Duplicate deploy option {option}.");
            }
        }

        var instance = options.GetValueOrDefault("--instance");
        var json = options.ContainsKey("--json");
        try
        {
            if (action == "status")
            {
                SeedGuard.Require((operation is null) != (instance is null), $"Usage: {DeployStatusUsage}");
                return await DeployStatusAsync(instance, operation, json, ct);
            }
            var tag = options.GetValueOrDefault("--release");
            var bundle = options.GetValueOrDefault("--bundle-dir");
            var trust = options.GetValueOrDefault("--trust");
            var prerelease = options.ContainsKey("--prerelease");
            SeedGuard.Require(instance is not null && ((tag is not null && bundle is null && trust is null)
                || (tag is null && bundle is not null && trust == DeployReleaseSource.LocalBuild))
                && (!prerelease || tag == "latest"), $"Usage: {DeployPlanUsage}");
            SeedGuard.Require(tag is null or "latest" || SeedRelease.IsReleaseTag(tag),
                "Deploy requires an exact Municode release tag or latest.");
            return await DeployPlanAsync(instance!, tag, bundle, prerelease, json, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _console.WriteError("Deploy interrupted. Any staged release and operation journal are retained.");
            return CliExitCodes.ValidationError;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException
            or InvalidOperationException or ArgumentException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Invalid deploy data in the release, operation journal or remote response.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Could not read the deploy release or access protected deploy files or SSH streams.");
        }
    }

    private async Task<int> DeployPlanAsync(
        string name, string? tag, string? bundle, bool prerelease, bool json, CancellationToken ct)
    {
        var instance = await _instances.ShowAsync(name, ct);
        var op = Guid.NewGuid().ToString("N");
        using var journal = new DeployJournal(_environment.ConfigHome, op, create: true);
        var downloads = Path.Combine(journal.DirectoryPath, "release");
        DeployRelease release;
        DeployOperation state;
        var transport = new DeployTransport(_instances, instance);
        try
        {
            release = await new DeployReleaseSource(_deployHttp, _deployVerifier)
                .ResolveAsync(tag, prerelease, bundle, downloads, ct);
            var plan = new DeployPlan(op, name, instance.Host.InstallationId, instance.Ssh.HostFingerprint,
                DeployReleaseSource.Protocol, release.Identity);
            state = new(1, plan, "staging", release.PublishedAt);
            journal.Save(state);
            if (!json)
            {
                if (release.PublishedAt is { } published)
                    _console.WriteLine($"Resolved latest to {release.Identity.Tag} (published {published:u}).");
                _console.WriteLine($"Verified {release.Identity.Tag} ({release.Identity.SourceRevision}) "
                    + $"using {release.Identity.Trust}; operation {op}.");
            }
            await transport.StageAsync(plan, release, ct);
            journal.Save(state = state with { Phase = "staged" });
        }
        finally
        {
            if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true);
        }

        var (preflight, blockers) = await transport.PreflightAsync(state.Plan, ct);
        string? digest = null;
        if (blockers.Length == 0)
        {
            digest = DeployApproval.ValidateAndCompute(state.Plan, instance.Host.Release, preflight);
            journal.Save(state = state with { Phase = "planned", ApprovalDigest = digest });
        }
        else
            journal.Save(state = state with { Phase = "blocked" });

        if (json)
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, operation = state, activeRelease = instance.Host.Release,
                preflight = DeployApproval.Summary(preflight), blockers
            }, SeedJournal.Json));
        else
        {
            _console.WriteLine("Staged on host; no services switched.");
            _console.WriteLine($"Current: {instance.Host.Release.ReleaseId} ({instance.Host.Release.SourceRevision})");
            _console.WriteLine($"Target:  {release.Identity.ReleaseId} ({release.Identity.SourceRevision}, {release.Identity.Trust})");
            PrintPreflight(preflight);
            foreach (var blocker in blockers) _console.WriteError($"Blocker: {blocker}");
            if (digest is not null) _console.WriteLine($"Approval digest: {digest}");
        }
        return digest is null ? CliExitCodes.ValidationError : CliExitCodes.Success;
    }

    private async Task<int> DeployStatusAsync(string? name, string? operation, bool json, CancellationToken ct)
    {
        DeployOperation? local = null;
        if (operation is not null)
        {
            using var journal = new DeployJournal(_environment.ConfigHome, operation);
            local = journal.Load();
            name = local.Plan.InstanceName;
        }
        var instance = await _instances.ShowAsync(name!, ct);
        SeedGuard.Require(local is null || (local.Plan.InstallationId == instance.Host.InstallationId
            && local.Plan.HostFingerprint == instance.Ssh.HostFingerprint),
            "The registered instance no longer matches this deploy operation.");
        var host = await new DeployTransport(_instances, instance).StatusAsync(operation, ct);
        var (version, versionStatus) = await ReadPublicDeployVersionAsync(instance.Host.ApiHostname, ct);
        if (json)
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, instanceName = name, activeRelease = instance.Host.Release,
                publicVersion = version, publicVersionStatus = versionStatus, operation = local, hostOperation = host
            }, SeedJournal.Json));
        else
        {
            var active = instance.Host.Release;
            _console.WriteLine($"Active: {active.ReleaseId} ({active.SourceRevision}, {active.Trust})");
            _console.WriteLine(version is null ? $"Public /health/version: {versionStatus}."
                : $"Public /health/version: {version.Tag} ({version.SourceRevision}, {version.Trust})");
            if (local is not null) _console.WriteLine($"Local operation {local.Plan.OperationId}: {local.Phase}");
            _console.WriteLine(host is null ? "No host operation state found."
                : $"Host operation {host.OperationId}: {host.Phase} ({host.Source})");
        }
        return CliExitCodes.Success;
    }

    private async Task<(PublicDeployVersion? Version, string Status)> ReadPublicDeployVersionAsync(string hostname, CancellationToken ct)
    {
        try
        {
            using var response = await _deployHttp.GetAsync(new UriBuilder("https", hostname) { Path = "/health/version" }.Uri, ct);
            if (!response.IsSuccessStatusCode) return (null, "unavailable");
            var version = JsonSerializer.Deserialize<PublicDeployVersion>(
                await response.Content.ReadAsByteArrayAsync(ct), SeedJournal.Json);
            if (version is null || version.Tag is null || !SeedRelease.IsReleaseTag(version.Tag)
                || version.SourceRevision is null || !Regex.IsMatch(version.SourceRevision, @"\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z")
                || version.Trust is not ("github-sigstore" or "local-build"))
                return (null, "invalid response");
            return (version, "available");
        }
        catch (HttpRequestException) { return (null, "unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "unavailable"); }
        catch (JsonException) { return (null, "invalid response"); }
    }

    private void PrintPreflight(JsonElement preflight)
    {
        static string Join(JsonElement array) => string.Join(", ", array.EnumerateArray().Select(item => item.GetString()));
        if (preflight.TryGetProperty("migrations", out var migrations))
            _console.WriteLine($"Migrations (PostgreSQL): {migrations.GetProperty("applied").GetArrayLength()} applied; "
                + $"{migrations.GetProperty("pending").GetArrayLength()} pending {Join(migrations.GetProperty("pending"))}".TrimEnd());
        if (preflight.TryGetProperty("digestInputs", out var digestInputs) && digestInputs.TryGetProperty("images", out var images))
            _console.WriteLine($"Target images: {Join(images)}");
        else if (preflight.TryGetProperty("imagesToPull", out var registryImages))
            _console.WriteLine($"Target registry images: {Join(registryImages)}");
        if (preflight.TryGetProperty("envDiff", out var env))
        {
            foreach (var field in env.EnumerateObject().Where(field => field.Value.GetArrayLength() > 0))
                _console.WriteLine($"Env {field.Name}: {Join(field.Value)}");
            _console.WriteLine("Policy: /etc/municode/config/municode.json (preserved)");
        }
        if (preflight.TryGetProperty("maintenance", out var work))
            _console.WriteLine($"Work: {work.GetProperty("nonterminalOperations").GetInt32()} nonterminal workspace operations; workspace VMs keep running.");
        if (preflight.TryGetProperty("openBao", out var bao))
            _console.WriteLine(bao.GetProperty("requiresUnsealKeyFile").GetBoolean()
                ? "Unseal: an offline unseal key file is required." : "Unseal: host unseal key present.");
        if (preflight.TryGetProperty("disk", out var disk))
            _console.WriteLine($"Backup: {disk.GetProperty("backupRequiredBytes").GetInt64():N0} bytes estimated; "
                + $"{disk.GetProperty("backupFreeBytes").GetInt64():N0} bytes free.");
        if (preflight.TryGetProperty("effectiveConfigHash", out _))
            _console.WriteLine("Downtime on apply: API, dashboard and gateway will be temporarily unavailable.");
    }
}
