// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly HashSet<string> SeedValues =
    [
        "--provider", "--region", "--size", "--dashboard-hostname", "--api-hostname",
        "--gateway-domain", "--release", "--credential-file", "--approve",
        "--confirm-console-fingerprint", "--instance-name"
    ];

    private async Task<int> RunSeedAsync(string[] args, CancellationToken cancellationToken)
    {
        var action = args.FirstOrDefault();
        if (action is not ("plan" or "apply" or "resume" or "status"))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: workspaces seed <plan|apply|resume|status> [operation] [options].");
        var operationId = action == "plan" ? null : args.ElementAtOrDefault(1);
        var optionOffset = action == "plan" ? 1 : 2;
        if (action != "plan")
            SeedGuard.Require(operationId is not null && !operationId.StartsWith("--", StringComparison.Ordinal),
                "Supply the recorded seed operation ID.");
        var options = ParseSeedOptions(args.Skip(optionOffset).ToArray());
        var allowed = action == "plan"
            ? SeedValues.Except(["--approve", "--confirm-console-fingerprint"]).ToHashSet()
            : action == "status"
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
            using var journal = new SeedJournal(_environment.ConfigHome, operationId, true);
            var selection = new SeedSelection(
                RequiredSeedOption(options, "--provider"),
                RequiredSeedOption(options, "--region"),
                RequiredSeedOption(options, "--size"),
                RequiredSeedOption(options, "--dashboard-hostname"),
                RequiredSeedOption(options, "--api-hostname"),
                RequiredSeedOption(options, "--gateway-domain"),
                RequiredSeedOption(options, "--release"));
            selection.Validate();
            var instanceName = options.GetValueOrDefault("--instance-name");
            InstanceRegistry.ValidateName(instanceName ?? selection.DashboardHostname);
            var credential = SeedFiles.ReadCredential(RequiredSeedOption(options, "--credential-file"));
            var release = await _seed.Release().VerifyAsync(
                selection.ReleaseTag, Path.Combine(journal.DirectoryPath, "release"), cancellationToken);
            using var provider = _seed.Providers(selection.Provider, credential, null);
            var quote = await provider.QuoteAsync(selection, operationId, cancellationToken);
            var state = new SeedState { Id = operationId, Plan = new(selection, quote, release.Identity, instanceName) };
            journal.Save(state);
            _console.WriteLine("Seed operation: " + operationId);
            foreach (var cost in quote.Costs)
            {
                _console.WriteLine("Cost: " + SeedGuard.FormatCost(cost));
                _console.WriteLine("  " + cost.Exclusions + " Source: " + cost.Source);
            }
            _console.WriteLine("One control-plane VPS only; no coding VM or provider credential registration.");
            _console.WriteLine("Exact release: " + release.Identity.Tag
                + " source " + release.Identity.SourceRevision
                + " manifest " + release.Identity.ManifestSha256
                + " bundle " + release.Identity.BundleSha256);
            _console.WriteLine("Approval digest: " + state.Plan.Approval);
            _console.WriteLine("No provider mutations. Review the costs and run: workspaces seed apply "
                + operationId + " --credential-file <owner-only-path> --approve " + state.Plan.Approval);
            return CliExitCodes.Success;
        }

        using (var journal = new SeedJournal(_environment.ConfigHome, operationId!))
        {
            var state = journal.Load();
            if (state.IsComplete)
            {
                await RegisterCompletedSeedAsync(state, journal, cancellationToken);
                _console.WriteLine("Seed infrastructure is already recorded complete. "
                    + "Run normal public HTTPS and server maintenance checks; do not provision another VPS.");
                return CliExitCodes.Success;
            }
            var credential = SeedFiles.ReadCredential(RequiredSeedOption(options, "--credential-file"));
            var release = await _seed.Release().VerifyAsync(
                state.Plan.Selection.ReleaseTag, Path.Combine(journal.DirectoryPath, "release"),
                cancellationToken);
            SeedGuard.Require(release.Identity == state.Plan.Release,
                "The exact release identity changed. Refusing to resume or install another bundle.");
            using var provider = _seed.Providers(state.Plan.Selection.Provider, credential, null);
            var lifecycle = new SeedLifecycle(provider, _seed.Host(), journal, release.Directory,
                _console.WriteLine);
            await lifecycle.ResumeAsync(state,
                options.GetValueOrDefault("--approve"),
                options.GetValueOrDefault("--confirm-console-fingerprint"),
                cancellationToken);
            if (state.IsComplete) await RegisterCompletedSeedAsync(state, journal, cancellationToken);
            return state.IsComplete ? CliExitCodes.Success : CliExitCodes.ValidationError;
        }
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
        _console.WriteLine("  workspaces seed plan --provider <hetzner|digitalocean|vultr> --region <id> --size <id>");
        _console.WriteLine("    --dashboard-hostname <name> --api-hostname <name> --gateway-domain <name>");
        _console.WriteLine("    --release <workspaces-vX.Y.Z> --credential-file <owner-only-path>");
        _console.WriteLine("    [--instance-name <name>] (defaults to the dashboard hostname)");
        _console.WriteLine("  workspaces seed apply <operation> --credential-file <path> --approve <digest>");
        _console.WriteLine("  workspaces seed resume <operation> --credential-file <path> [--confirm-console-fingerprint <SHA256:...>]");
        _console.WriteLine("  workspaces seed status <operation>");
        _console.WriteLine("Compatibility: 'municode seed ...' and 'municode workspaces seed ...' use the same command.");
        _console.WriteLine("Plan is read-only except for protected local state and exact release verification.");
        _console.WriteLine("Seed provisions one control-plane VPS only. It never registers provider credentials in the control plane or creates coding VMs.");
    }
}
