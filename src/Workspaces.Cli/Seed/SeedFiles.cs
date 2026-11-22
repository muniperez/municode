// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Municode.Cli.Commands;

namespace Municode.Cli.Seed;

internal static class SeedFiles
{
    private const UnixFileMode ProtectedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ProtectedFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(parent)) CreateDirectory(parent);
            RejectUnsafe(parent);
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(full);
            else
                Directory.CreateDirectory(full, ProtectedDirectoryMode);
        }
        Check(full, true);
    }

    public static void Check(string path, bool directory = false) => Check(path, directory, false);

    public static void CheckPublicKey(string path) => Check(path, false, true);

    private static void Check(string path, bool directory, bool publicKey)
    {
        RejectUnsafe(path);
        var info = File.GetAttributes(path);
        SeedGuard.Require(info.HasFlag(FileAttributes.Directory) == directory,
            directory ? "Expected a protected directory." : "Expected a regular protected file.");
        SeedGuard.Require(!info.HasFlag(FileAttributes.ReparsePoint), "Seed paths must not contain links.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if (directory)
                SeedGuard.Require(mode == ProtectedDirectoryMode, "Seed directories require mode 0700.");
            else if (publicKey)
                SeedGuard.Require(mode is ProtectedFileMode or UnixFileMode.UserRead
                    || mode == (ProtectedFileMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
                    || mode == (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                    "Seed public keys require mode 0600, 0400, 0644 or 0444.");
            else
                SeedGuard.Require(mode is ProtectedFileMode or UnixFileMode.UserRead,
                    "Seed files require owner-only mode 0600 or 0400.");
        }
    }

    public static FileStream CreateNew(string path)
    {
        CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = ProtectedFileMode;
        return new FileStream(path, options);
    }

    public static void WriteNew(string path, ReadOnlySpan<byte> data)
    {
        using var stream = CreateNew(path);
        stream.Write(data);
        stream.Flush(true);
    }

    public static void ReplaceAtomically(string path, ReadOnlySpan<byte> data)
    {
        CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pending = path + ".pending";
        if (File.Exists(pending)) throw new CliCommandException(
            CliExitCodes.ValidationError,
            "An interrupted seed write remains. Preserve operation.json and operation.json.pending; do not reset the mutation journal.");
        using (var stream = CreateNew(pending))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(pending, path, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, ProtectedFileMode);
    }

    public static string ReadCredential(string path)
    {
        Check(path);
        SeedGuard.Require(new FileInfo(path).Length is > 0 and <= 16_384,
            "The provider credential file must contain one bounded API token.");
        var value = File.ReadAllText(path).Trim();
        SeedGuard.Require(value.Length > 0 && !value.Any(char.IsWhiteSpace),
            "The provider credential must be one nonempty value; use an owner-only file, not a command-line argument.");
        return value;
    }

    private static void RejectUnsafe(string path)
    {
        var full = Path.GetFullPath(path);
        SeedGuard.Require(!full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Contains(".."),
            "Seed paths must not contain parent traversal.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            SeedGuard.Require(!File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint),
                "Seed paths must not contain symbolic links or reparse points.");
            if (!OperatingSystem.IsWindows() && Directory.Exists(current))
                SeedGuard.Require((File.GetUnixFileMode(current) &
                    (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                    "Seed paths must not have group/world-writable ancestors.");
        }
    }
}

internal sealed class SeedJournal : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private FileStream? _lock;

    public SeedJournal(string home, string id, bool create = false)
    {
        SeedGuard.Require(System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{32}$"),
            "Seed operation IDs are exactly 32 lowercase hexadecimal characters.");
        DirectoryPath = Path.Combine(home, "seed", id);
        if (create) SeedFiles.CreateDirectory(DirectoryPath);
        else SeedFiles.Check(DirectoryPath, true);
        var lockPath = Path.Combine(DirectoryPath, "operation.lock");
        if (create && !File.Exists(lockPath)) SeedFiles.WriteNew(lockPath, []);
        SeedFiles.Check(lockPath);
        try
        {
            _lock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "This seed operation is already active. Do not remove the persistent operation lock.");
        }
    }

    public string DirectoryPath { get; }

    public SeedState Load()
    {
        var path = Path.Combine(DirectoryPath, "operation.json");
        SeedGuard.Require(!File.Exists(path + ".pending"),
            "An interrupted journal write remains. Preserve and reconcile operation.json plus operation.json.pending.");
        SeedFiles.Check(path);
        var state = JsonSerializer.Deserialize<SeedState>(File.ReadAllText(path), Json)
            ?? throw new JsonException("The seed journal is empty.");
        SeedGuard.Require(state.Schema == 1 && state.Id == Path.GetFileName(DirectoryPath),
            "The seed journal is unsupported or belongs to another operation.");
        state.Plan.Selection.Validate();
        SeedGuard.Require(state.Plan.Release.Tag == state.Plan.Selection.ReleaseTag
            && state.Plan.Quote.Costs is { Length: > 0 }
            && state.Plan.Quote.Costs.All(cost => cost.Monthly > 0 && !string.IsNullOrWhiteSpace(cost.Currency)),
            "The seed plan is incomplete or corrupt.");
        SeedGuard.Require(state.Phase is "planned" or "creating" or "awaiting_fingerprint" or "preparing"
            or "awaiting_dns" or "installing" or "verifying" or "complete" or "reconciliation_required" or "failed",
            "The seed journal has an unknown lifecycle phase.");
        SeedGuard.Require(state.Server is null || state.ServerCreateStarted,
            "The seed journal records a server without durable creation intent.");
        SeedGuard.Require(!state.InstallStarted || state.Server?.Address is not null
            && state.HostFingerprint is not null && state.HostPrepared,
            "The seed journal has conflicting installation lifecycle flags.");
        return state;
    }

    public void Save(SeedState state)
    {
        SeedGuard.Require(state.Id == Path.GetFileName(DirectoryPath), "Refusing to save a journal to another operation.");
        SeedFiles.ReplaceAtomically(Path.Combine(DirectoryPath, "operation.json"),
            JsonSerializer.SerializeToUtf8Bytes(state, Json));
    }

    public void Dispose() => _lock?.Dispose();
}
