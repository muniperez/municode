// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Deploy;

internal sealed record DeployOperation(
    int Schema, DeployPlan Plan, string Phase, DateTimeOffset? PublishedAt = null,
    string? ApprovalDigest = null, DeployExecution? Execution = null, string? PendingStep = null);

internal sealed record DeployExecution(
    string Action, int? DrainTimeout = null, string? OffhostBackup = null, bool UsesUnsealKey = false,
    bool RestoreDatabase = false, string? AcceptDataLossSince = null, bool AcceptProviderReconciliation = false);

internal sealed class DeployJournal : IDisposable
{
    private readonly FileStream _lock;

    public DeployJournal(string home, string id, bool create = false)
    {
        DirectoryPath = OperationDirectory(home, id);
        if (create) SeedFiles.CreateDirectory(DirectoryPath);
        else SeedGuard.Require(Directory.Exists(DirectoryPath), "No local journal exists for that deploy operation.");
        SeedFiles.Check(DirectoryPath, true);
        var path = Path.Combine(DirectoryPath, "operation.lock");
        if (create) SeedFiles.WriteNew(path, []);
        SeedFiles.Check(path);
        try
        {
            _lock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError,
                "This deploy operation is already active. Do not remove its operation lock.");
        }
    }

    public string DirectoryPath { get; }
    private string StatePath => Path.Combine(DirectoryPath, "operation.json");

    public static DeployOperation Read(string home, string id) => ReadDirectory(OperationDirectory(home, id));

    public DeployOperation Load() => ReadDirectory(DirectoryPath);

    private static string OperationDirectory(string home, string id)
    {
        SeedGuard.Require(Regex.IsMatch(id, @"\A[a-f0-9]{32}\z"),
            "Deploy operation IDs are exactly 32 lowercase hexadecimal characters.");
        return Path.Combine(home, "deploy", id);
    }

    private static DeployOperation ReadDirectory(string directory)
    {
        SeedGuard.Require(Directory.Exists(directory), "No local journal exists for that deploy operation.");
        SeedFiles.Check(directory, true);
        var path = Path.Combine(directory, "operation.json");
        SeedGuard.Require(File.Exists(path), "The deploy operation has no verified release journal.");
        SeedFiles.Check(path);
        var state = JsonSerializer.Deserialize<DeployOperation>(File.ReadAllText(path), SeedJournal.Json);
        SeedGuard.Require(state is { Schema: 1, Plan.TargetRelease: not null }
            && state.Plan.OperationId == Path.GetFileName(directory)
            && InstancePin.IsFingerprint(state.Plan.HostFingerprint)
            && !string.IsNullOrEmpty(state.Plan.InstallationId),
            "The deploy journal has an invalid operation identity.");
        return state!;
    }

    public void Save(DeployOperation state) =>
        SeedFiles.ReplaceAtomically(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, SeedJournal.Json));

    public void Dispose() => _lock.Dispose();
}
