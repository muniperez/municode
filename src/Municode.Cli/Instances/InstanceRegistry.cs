// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Instances;

internal sealed record InstanceConnection(
    string Destination, string? IdentityFile, string HostFingerprint, string? SeedOperationId = null);

internal sealed record InstanceEntry(string Name, InstanceConnection Ssh, InstanceHost Host);

internal sealed class InstanceRegistry(string configHome, ISeedProcess process)
{
    private readonly string _root = Path.GetFullPath(Path.Combine(configHome, "instances"));
    private readonly InstanceSsh _ssh = new(process);

    public static void ValidateName(string name) =>
        SeedGuard.Require(Regex.IsMatch(name, @"\A[a-z0-9][a-z0-9._-]{0,252}\z")
            && !name.EndsWith('.')
            && !Regex.IsMatch(name, @"\A(?:con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|\z)"),
            "Use a lowercase instance name with letters, digits, dots, underscores or hyphens.");

    public async Task<InstanceEntry> AddAsync(
        string name, string destination, string? identityFile, string? fingerprint,
        Func<string, bool>? confirm, CancellationToken ct)
    {
        ValidateName(name);
        InstanceSsh.ValidateDestination(destination);
        SeedGuard.Require(fingerprint is not null || confirm is not null,
            "Non-interactive instance add requires --confirm-host-fingerprint SHA256:... from a trusted source.");
        SeedGuard.Require(fingerprint is null || InstancePin.IsFingerprint(fingerprint),
            "Supply an exact SHA256 SSH host fingerprint.");
        var identityPath = identityFile is null ? null : Path.GetFullPath(identityFile);
        SeedGuard.Require(identityPath is null || File.Exists(identityPath),
            "The SSH identity file does not exist.");

        using var registryLock = Lock();
        RequireAvailable(name);
        var pending = CreatePending();
        var knownHosts = Path.Combine(pending, "known_hosts");
        try
        {
            SeedFiles.WriteNew(knownHosts, []);
            var pin = await _ssh.DiscoverAsync(destination, knownHosts, ct);
            SeedGuard.Require(fingerprint is not null
                    ? pin.Fingerprint == fingerprint
                    : confirm!(pin.Fingerprint),
                "The SSH host fingerprint was not confirmed. No instance was registered.");
            var connection = new InstanceConnection(destination, identityPath, pin.Fingerprint);
            return await RegisterAsync(name, connection, pending, ct);
        }
        finally
        {
            DeletePending(pending);
        }
    }

    public async Task<InstanceEntry> RegisterSeedAsync(SeedState state, string seedDirectory, CancellationToken ct)
    {
        var name = state.Plan.InstanceName ?? state.Plan.Selection.DashboardHostname;
        ValidateName(name);
        var address = state.Server?.Address;
        var fingerprint = state.HostFingerprint;
        SeedGuard.Require(address is not null && fingerprint is not null && state.InstallationId is not null,
            "The completed seed journal is missing its SSH or installation identity.");
        var identity = Path.Combine(seedDirectory, "id_ed25519");
        var seedKnownHosts = Path.Combine(seedDirectory, "known_hosts");
        SeedGuard.Require(File.Exists(seedKnownHosts) && File.Exists(identity),
            "The completed seed is missing its protected SSH files. Preserve the seed journal.");
        SeedFiles.Check(seedKnownHosts);
        var pin = InstancePin.Parse(File.ReadAllText(seedKnownHosts), address!);
        SeedGuard.Require(pin.Fingerprint == fingerprint, "The seed's pinned SSH host key does not match its journal.");
        var connection = new InstanceConnection("root@" + address, Path.GetFullPath(identity), pin.Fingerprint, state.Id);

        using var registryLock = Lock();
        if (Path.Exists(EntryPath(name)))
        {
            // A prior run may have published the entry before updating the seed journal.
            var existing = Load(name);
            if (existing.Ssh == connection && existing.Host.InstallationId == state.InstallationId)
                return existing;
            RequireAvailable(name);
        }
        var pending = CreatePending();
        try
        {
            SeedFiles.WriteNew(Path.Combine(pending, "known_hosts"), Encoding.UTF8.GetBytes(pin.Line));
            return await RegisterAsync(name, connection, pending, ct, state.InstallationId);
        }
        finally
        {
            DeletePending(pending);
        }
    }

    public InstanceEntry[] List()
    {
        if (!Path.Exists(_root)) return [];
        using var registryLock = Lock();
        return Directory.EnumerateDirectories(_root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith(".pending-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(Load)
            .ToArray();
    }

    public async Task<InstanceEntry> ShowAsync(string name, CancellationToken ct)
    {
        ValidateName(name);
        RequireRegistry();
        using var registryLock = Lock();
        var entry = Load(name);
        var host = await _ssh.ProbeAsync(entry.Ssh, Path.Combine(EntryPath(name), "known_hosts"), ct);
        SeedGuard.Require(host.InstallationId == entry.Host.InstallationId,
            "The host installation ID changed. Remove and explicitly re-register this instance if expected.");
        return entry with { Host = host };
    }

    public InstanceEntry Remove(string name)
    {
        ValidateName(name);
        RequireRegistry();
        using var registryLock = Lock();
        var entry = Load(name);
        var path = EntryPath(name);
        SeedGuard.Require(Directory.EnumerateFileSystemEntries(path)
            .All(file => Path.GetFileName(file) is "instance.json" or "known_hosts"),
            "The instance directory has unexpected files. Preserve them before removing the entry.");
        var pending = PendingPath();
        Directory.Move(path, pending);
        DeletePending(pending);
        return entry;
    }

    private async Task<InstanceEntry> RegisterAsync(string name, InstanceConnection connection,
        string pending, CancellationToken ct, string? expectedInstallationId = null)
    {
        var host = await _ssh.ProbeAsync(connection, Path.Combine(pending, "known_hosts"), ct);
        SeedGuard.Require(expectedInstallationId is null || host.InstallationId == expectedInstallationId,
            "The SSH host installation ID does not match the completed seed journal.");
        var entry = new InstanceEntry(name, connection, host);
        SeedFiles.WriteNew(Path.Combine(pending, "instance.json"), JsonSerializer.SerializeToUtf8Bytes(entry, SeedJournal.Json));
        Directory.Move(pending, EntryPath(name));
        return entry;
    }

    private InstanceEntry Load(string name)
    {
        ValidateName(name);
        var path = EntryPath(name);
        SeedGuard.Require(Path.Exists(path), $"No instance named '{name}' is registered.");
        SeedFiles.Check(path, true);
        var data = Path.Combine(path, "instance.json");
        SeedFiles.Check(data);
        var entry = JsonSerializer.Deserialize<InstanceEntry>(File.ReadAllText(data), SeedJournal.Json);
        SeedGuard.Require(entry?.Name == name && entry.Ssh is not null && entry.Host is not null
            && InstancePin.IsFingerprint(entry.Ssh.HostFingerprint)
            && !string.IsNullOrEmpty(entry.Ssh.Destination)
            && (entry.Ssh.IdentityFile is null || Path.IsPathFullyQualified(entry.Ssh.IdentityFile)),
            "The instance registry entry is invalid.");
        SeedFiles.Check(Path.Combine(path, "known_hosts"));
        return entry!;
    }

    private FileStream Lock()
    {
        SeedFiles.CreateDirectory(_root);
        var path = Path.Combine(_root, ".lock");
        if (Path.Exists(path)) SeedFiles.Check(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            return new FileStream(path, options);
        }
        catch (IOException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Could not lock the instance registry. Check for another active instance command.");
        }
    }

    private void RequireRegistry() =>
        SeedGuard.Require(Path.Exists(_root), "No instances are registered.");

    private void RequireAvailable(string name) =>
        SeedGuard.Require(!Path.Exists(EntryPath(name)), $"Instance '{name}' is already registered.");

    private string EntryPath(string name) => Path.Combine(_root, name);

    private string PendingPath() => Path.Combine(_root, ".pending-" + Guid.NewGuid().ToString("N"));

    private string CreatePending()
    {
        var pending = PendingPath();
        SeedFiles.CreateDirectory(pending);
        return pending;
    }

    private static void DeletePending(string pending)
    {
        if (!Directory.Exists(pending)) return;
        File.Delete(Path.Combine(pending, "known_hosts"));
        File.Delete(Path.Combine(pending, "instance.json"));
        Directory.Delete(pending);
    }
}
