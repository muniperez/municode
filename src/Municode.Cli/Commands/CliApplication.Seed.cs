// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli.Deploy;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly HashSet<string> SeedValues =
    [
        "--provider", "--region", "--size", "--dashboard-hostname", "--api-hostname",
        "--gateway-domain", "--release", "--credential-file", "--approve",
        "--confirm-console-fingerprint", "--instance-name", "--workspace-provider",
        "--bundle-dir", "--trust"
    ];

    private async Task<int> RunSeedAsync(string[] args, CancellationToken cancellationToken)
    {
        var action = args.FirstOrDefault();
        if (action is not ("plan" or "apply" or "resume" or "status" or "rotate-setup-token"))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: municode seed <plan|apply|resume|status|rotate-setup-token> [operation] [options].");
        var operationId = action == "plan" ? null : args.ElementAtOrDefault(1);
        var optionOffset = action == "plan" ? 1 : 2;
        if (action != "plan")
            SeedGuard.Require(operationId is not null && !operationId.StartsWith("--", StringComparison.Ordinal),
                "Supply the recorded seed operation ID.");
        var options = ParseSeedOptions(args.Skip(optionOffset).ToArray());
        var allowed = action == "plan"
            ? SeedValues.Except(["--approve", "--confirm-console-fingerprint"]).ToHashSet()
            : action is "status" or "rotate-setup-token"
                ? []
                : new HashSet<string>(["--credential-file", "--approve", "--confirm-console-fingerprint"]);
        SeedGuard.Require(options.Keys.All(allowed.Contains),
            "That option is not valid for this seed action. Selection is immutable after plan.");
        if (action == "status")
        {
            using var statusJournal = new SeedJournal(_environment.ConfigHome, operationId!);
            var status = statusJournal.Load();
            _console.WriteLine(JsonSerializer.Serialize(status, SeedJournal.Json));
            _console.WriteLine("Local journal only, not live provider health. Any created VPS may remain billable.");
            return CliExitCodes.Success;
        }

        if (action == "plan")
        {
            operationId = Guid.NewGuid().ToString("N");
            var tag = options.GetValueOrDefault("--release");
            var bundleDirectory = options.GetValueOrDefault("--bundle-dir");
            var trust = options.GetValueOrDefault("--trust");
            SeedGuard.Require((tag is not null && bundleDirectory is null && trust is null)
                || (tag is null && bundleDirectory is not null && trust == DeployReleaseSource.LocalBuild),
                "Select exactly one release: --release <municode-vX.Y.Z>, or an unsigned local build with "
                + "--bundle-dir <dir> --trust local-build.");
            // Verify a local build before creating any journal so a rejected bundle leaves no state.
            var localRelease = bundleDirectory is null ? null
                : await _seed.Release().VerifyLocalAsync(bundleDirectory, cancellationToken);
            using var journal = new SeedJournal(_environment.ConfigHome, operationId, true);
            var selection = new SeedSelection(
                RequiredSeedOption(options, "--provider"),
                RequiredSeedOption(options, "--region"),
                RequiredSeedOption(options, "--size"),
                RequiredSeedOption(options, "--dashboard-hostname"),
                RequiredSeedOption(options, "--api-hostname"),
                RequiredSeedOption(options, "--gateway-domain"),
                localRelease?.Identity.Tag ?? tag!);
            selection.Validate();
            var instanceName = options.GetValueOrDefault("--instance-name");
            InstanceRegistry.ValidateName(instanceName ?? selection.DashboardHostname);
            var credential = SeedFiles.ReadCredential(RequiredSeedOption(options, "--credential-file"));
            var register = WorkspaceProviderChoice(options.GetValueOrDefault("--workspace-provider"), selection.Provider);
            var release = localRelease ?? await _seed.Release().VerifyAsync(
                selection.ReleaseTag, Path.Combine(journal.DirectoryPath, "release"), cancellationToken);
            using var provider = _seed.Providers(selection.Provider, credential, null);
            var quote = await provider.QuoteAsync(selection, operationId, register, cancellationToken);
            var state = new SeedState
            {
                Id = operationId, Plan = new(selection, quote, release.Identity, instanceName),
                RegisterWorkspaceProvider = register,
                LocalBundleDirectory = localRelease?.Directory
            };
            journal.Save(state);
            _console.WriteLine("Seed operation: " + operationId);
            foreach (var cost in quote.Costs)
            {
                _console.WriteLine("Cost: " + SeedGuard.FormatCost(cost));
                _console.WriteLine("  " + cost.Exclusions + " Source: " + cost.Source);
            }
            _console.WriteLine("One control-plane VPS only; no coding VM.");
            if (quote.Workspace is { } workspace)
            {
                _console.WriteLine($"Workspace provider: this {selection.Provider} account is registered as "
                    + "'Default provider' in the first organization after first-owner setup, with a default "
                    + $"{workspace.Sku} plan in {selection.Region} on stock Ubuntu 24.04. Users can launch coding VMs immediately.");
                _console.WriteLine("Cost: " + SeedGuard.FormatCost(workspace.Cost));
                _console.WriteLine("  " + workspace.Cost.Exclusions + " Source: " + workspace.Cost.Source);
            }
            else
                _console.WriteLine("Workspace provider: not registered. Add one in organization settings after setup.");
            if (localRelease is not null)
                _console.WriteLine("Unsigned local build (trust local-build, not a signed GitHub release): "
                    + $"local-{release.Identity.SourceRevision[..12]}-{release.Identity.BundleSha256[..12]} from "
                    + release.Directory);
            _console.WriteLine("Exact release: " + release.Identity.Tag
                + " source " + release.Identity.SourceRevision
                + " manifest " + release.Identity.ManifestSha256
                + " bundle " + release.Identity.BundleSha256);
            _console.WriteLine("Approval digest: " + state.Plan.Approval);
            _console.WriteLine("No provider mutations. Review the costs and run: municode seed apply "
                + operationId + " --credential-file <owner-only-path> --approve " + state.Plan.Approval);
            return CliExitCodes.Success;
        }

        using (var journal = new SeedJournal(_environment.ConfigHome, operationId!))
        {
            var state = journal.Load();
            if (action == "rotate-setup-token")
            {
                SeedGuard.Require(state.IsComplete && state.InstallationId is not null
                    && state.HostFingerprint is not null && state.Server?.Address is not null,
                    "Setup token rotation requires a completed seed with a pinned SSH host.");
                await _seed.Host().RotateSetupTokenAsync(state, journal.DirectoryPath, cancellationToken);
                var tokenPath = Path.Combine(journal.DirectoryPath, "setup-token");
                var dashboard = state.Plan.Selection.DashboardHostname;
                if (_console.IsInteractive && SeedLifecycle.SetupLink(dashboard, tokenPath) is { } link)
                    _console.WriteLine($"First owner: open {link} to finish setup. Treat this link like a password.");
                else
                    _console.WriteLine($"Setup token saved to {tokenPath}. Enter it at https://{dashboard}/setup.");
                return CliExitCodes.Success;
            }
            if (state.IsComplete)
            {
                await RegisterCompletedSeedAsync(state, journal, cancellationToken);
                _console.WriteLine("Seed infrastructure is already recorded complete. "
                    + "Run normal public HTTPS and server maintenance checks; do not provision another VPS.");
                return CliExitCodes.Success;
            }
            var credential = SeedFiles.ReadCredential(RequiredSeedOption(options, "--credential-file"));
            SeedGuard.Require((state.LocalBundleDirectory is not null)
                == (state.Plan.Release.Trust == DeployReleaseSource.LocalBuild),
                "The seed journal's release trust and local bundle location disagree.");
            var release = state.LocalBundleDirectory is { } localDirectory
                ? await _seed.Release().VerifyLocalAsync(localDirectory, cancellationToken)
                : await _seed.Release().VerifyAsync(
                    state.Plan.Selection.ReleaseTag, Path.Combine(journal.DirectoryPath, "release"),
                    cancellationToken);
            SeedGuard.Require(release.Identity == state.Plan.Release,
                "The exact release identity changed. Refusing to resume or install another bundle.");
            using var provider = _seed.Providers(state.Plan.Selection.Provider, credential, null);
            var lifecycle = new SeedLifecycle(provider, _seed.Host(), journal, release.Directory,
                _console.WriteLine, state.RegisterWorkspaceProvider ? credential : null,
                _console.IsInteractive && _console.IsOutputTerminal);
            await lifecycle.ResumeAsync(state,
                options.GetValueOrDefault("--approve"),
                options.GetValueOrDefault("--confirm-console-fingerprint"),
                cancellationToken);
            if (state.IsComplete) await RegisterCompletedSeedAsync(state, journal, cancellationToken);
            return state.IsComplete ? CliExitCodes.Success : CliExitCodes.ValidationError;
        }
    }

    private bool WorkspaceProviderChoice(string? option, string provider)
    {
        if (option is not null)
            return option switch
            {
                "yes" => true,
                "no" => false,
                _ => throw new CliCommandException(CliExitCodes.ValidationError, "Use --workspace-provider yes or no.")
            };
        // Unattended seed never prompts and keeps the default.
        if (!_console.IsInteractive) return true;
        _console.WriteError($"Also use this {provider} account as the workspace provider, with a default plan so users "
            + "can launch coding VMs right after first-owner setup? Seed creates no coding VM either way. [Y/n]");
        return _console.ReadLine()?.Trim().ToLowerInvariant() is not ("n" or "no");
    }

    private async Task RegisterCompletedSeedAsync(SeedState state, SeedJournal journal, CancellationToken ct)
    {
        if (state.InstanceRegistered) return;
        try
        {
            var entry = await _instances.RegisterSeedAsync(state, journal.DirectoryPath, ct);
            state.InstanceRegistered = true;
            journal.Save(state);
            _console.WriteLine($"Registered instance '{entry.Name}'.");
        }
        catch (CliCommandException error)
        {
            throw new CliCommandException(error.ExitCode, "Seed installation completed, but local instance "
                + $"registration failed: {error.Message} To retry registration, run 'municode seed resume {state.Id}'.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "Seed installation completed, but local "
                + $"instance registration could not be saved. Check protected files and retry 'municode seed resume {state.Id}'.");
        }
    }

    private static Dictionary<string, string> ParseSeedOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            SeedGuard.Require(SeedValues.Contains(name),
                "Unknown seed option. Provider credentials are accepted only with --credential-file.");
            SeedGuard.Require(index + 1 < args.Length
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal),
                $"Seed option {name} requires a value.");
            var value = args[++index];
            SeedGuard.Require(options.TryAdd(name, value),
                "Duplicate seed options are not allowed.");
        }
        return options;
    }

    private static string RequiredSeedOption(IReadOnlyDictionary<string, string> options, string name)
    {
        SeedGuard.Require(options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value),
            $"Required seed option: {name} <value>.");
        return value!;
    }

    private void PrintSeedHelp()
    {
        _console.WriteLine("Usage:");
        _console.WriteLine("  municode seed plan --provider <hetzner|digitalocean|vultr> --region <id> --size <id>");
        _console.WriteLine("    --dashboard-hostname <name> --api-hostname <name> --gateway-domain <name>");
        _console.WriteLine("    (--release <municode-vX.Y.Z> | --bundle-dir <dir> --trust local-build)");
        _console.WriteLine("    --credential-file <owner-only-path>");
        _console.WriteLine("    [--instance-name <name>] (defaults to the dashboard hostname) [--workspace-provider <yes|no>]");
        _console.WriteLine("  municode seed apply <operation> --credential-file <path> --approve <digest>");
        _console.WriteLine("  municode seed resume <operation> --credential-file <path> [--confirm-console-fingerprint <SHA256:...>]");
        _console.WriteLine("  municode seed status <operation>");
        _console.WriteLine("  municode seed rotate-setup-token <operation>");
        _console.WriteLine("Plan is read-only except for protected local state and exact release verification.");
        _console.WriteLine("--bundle-dir --trust local-build seeds an unsigned scripts/delivery/build-release.sh output");
        _console.WriteLine("after the same manifest, checksum, image and archive checks as deploy; signed releases are the default.");
        _console.WriteLine("Seed provisions one control-plane VPS only and never creates coding VMs.");
        _console.WriteLine("Plan asks whether to reuse the same provider account for coding VMs (default yes); unattended plan keeps the default.");
        _console.WriteLine("On yes, the control plane registers it as 'Default provider' with a default stock-Ubuntu workspace plan");
        _console.WriteLine("after first-owner setup, so users can launch coding VMs immediately. --workspace-provider no opts out.");
    }
}
