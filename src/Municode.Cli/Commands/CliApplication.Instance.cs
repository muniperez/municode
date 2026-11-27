// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunInstanceAsync(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: municode instance <add|list|show|remove> [name] [options].";
        var action = args.FirstOrDefault();
        SeedGuard.Require(action is "add" or "list" or "show" or "remove", usage);
        var name = action == "list" ? null : args.ElementAtOrDefault(1);
        SeedGuard.Require(action == "list" || name is not null && !name.StartsWith('-'), usage);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var index = action == "list" ? 1 : 2; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--json")
            {
                SeedGuard.Require(!json, "Duplicate instance option --json.");
                json = true;
                continue;
            }
            SeedGuard.Require(action == "add"
                && option is "--ssh" or "--identity-file" or "--confirm-host-fingerprint",
                "Unknown instance option. See 'municode help instance'.");
            SeedGuard.Require(index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal), $"{option} requires a value.");
            SeedGuard.Require(options.TryAdd(option, args[++index]), $"Duplicate instance option {option}.");
        }

        try
        {
            if (action == "list")
            {
                var entries = _instances.List();
                if (json)
                    _console.WriteLine(JsonSerializer.Serialize(entries, SeedJournal.Json));
                else if (entries.Length == 0)
                    _console.WriteLine("No instances registered.");
                else
                    foreach (var entry in entries) PrintInstance(entry, false);
                return CliExitCodes.Success;
            }
            var instance = action switch
            {
                "add" => await _instances.AddAsync(name!,
                    options.GetValueOrDefault("--ssh") ?? throw new CliCommandException(
                        CliExitCodes.ValidationError, "Instance add requires --ssh <destination>."),
                    options.GetValueOrDefault("--identity-file"),
                    options.GetValueOrDefault("--confirm-host-fingerprint"),
                    _console.IsInteractive ? ConfirmInstanceFingerprint : null, ct),
                "show" => await _instances.ShowAsync(name!, ct),
                _ => _instances.Remove(name!)
            };
            if (json)
                _console.WriteLine(JsonSerializer.Serialize(instance, SeedJournal.Json));
            else if (action == "remove")
                _console.WriteLine($"Removed local registration for '{instance.Name}'. The host was not changed.");
            else
                PrintInstance(instance, true);
            return CliExitCodes.Success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Could not read or update the protected instance registry. Check its files and permissions.");
        }
    }

    private bool ConfirmInstanceFingerprint(string fingerprint)
    {
        _console.WriteError($"SSH host fingerprint: {fingerprint}");
        _console.WriteError("Verify it independently, then type the exact fingerprint to continue:");
        return _console.ReadLine()?.Trim() == fingerprint;
    }

    private void PrintInstance(InstanceEntry entry, bool details)
    {
        _console.WriteLine($"Instance {entry.Name}: {entry.Host.ApiHostname}, SSH {entry.Ssh.Destination} "
            + $"(pinned {entry.Ssh.HostFingerprint})");
        if (!details) return;
        var release = entry.Host.Release;
        var source = entry.Ssh.SeedOperationId is null ? "registered over SSH" : "installed by seed";
        _console.WriteLine($"Release {release.Tag ?? release.ReleaseId} ({release.SourceRevision}), {source}");
        _console.WriteLine($"Dashboard: {entry.Host.DashboardHostname}; gateway: {entry.Host.GatewayDomain}; "
            + $"installation ID: {entry.Host.InstallationId}");
    }

    private void PrintInstanceHelp()
    {
        _console.WriteLine("Usage:");
        _console.WriteLine("  municode instance add <name> --ssh <destination> [--identity-file <path>]");
        _console.WriteLine("    [--confirm-host-fingerprint SHA256:...] [--json]");
        _console.WriteLine("  municode instance list [--json]");
        _console.WriteLine("  municode instance show <name> [--json]");
        _console.WriteLine("  municode instance remove <name> [--json]");
        _console.WriteLine("Add and show probe the host read-only. List and remove only access the local registry.");
    }
}
