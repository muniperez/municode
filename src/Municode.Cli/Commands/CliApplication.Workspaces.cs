// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Municode.Cli.Api;
using Municode.Cli.Contracts;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private static readonly JsonSerializerOptions WorkspaceJson = new(JsonSerializerDefaults.Web);

    private async Task<int> RunWorkspacesAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        if (action == "seed")
            return await RunSeedAsync(args.Skip(1).ToArray(), ct);
        if (action is not ("list" or "selections" or "create" or "open" or "status" or "delete" or "start" or "stop"
            or "cancel-idle-stop" or "idle-timeout" or "resize-next-start" or "vms" or "launch" or "vm-status"
            or "vm-start" or "vm-stop" or "vm-cancel-idle-stop" or "vm-discard" or "vm-resize-next-start"
            or "stop-all" or "task-renew" or "task-complete"))
            throw WorkspaceUsage("Expected list, selections, create, open, status, delete, start, stop, cancel-idle-stop, "
                + "idle-timeout, resize-next-start, vms, launch, vm-status, vm-start, vm-stop, vm-cancel-idle-stop, "
                + "vm-discard, vm-resize-next-start, stop-all, task-renew or task-complete. See workspaces --help.");

        string Required(string option) =>
            GetOption(args, option) is { Length: > 0 } value
                ? value
                : throw WorkspaceUsage($"Required: {option} <value>.");
        long Generation() =>
            long.TryParse(Required("--generation"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : throw WorkspaceUsage("--generation must identify the current VM generation (non-negative integer).");
        void Acknowledge(string flag)
        {
            if (!args.Contains(flag, StringComparer.Ordinal))
                throw WorkspaceUsage($"Explicit {flag} is required.");
        }
        var json = args.Contains("--json", StringComparer.Ordinal);
        void Note(string message)
        {
            if (json) _console.WriteError(message);
            else _console.WriteLine(message);
        }
        int Output<T>(T value, Action<T> human)
        {
            if (json) _console.WriteLine(JsonSerializer.Serialize(value, WorkspaceJson));
            else human(value);
            return CliExitCodes.Success;
        }
        int? IdleTimeoutOption(string option) =>
            GetOption(args, option) is not { } raw ? null
                : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10_080
                    ? minutes
                    : throw WorkspaceUsage($"{option} must be a whole number of minutes from 1 to 10080 (7 days).");
        DevelopmentWorkspaceResizeRequest Resize() =>
            new(Generation(), Required("--image"), Required("--profile"), Required("--plan"));

        if (action == "delete") ValidateWorkspaceDeletion(args);
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null && me.Organizations.Count == 1) organizationId = me.Organizations[0].Id;
        if (organizationId is null || !me.Organizations.Any(x => x.Id == organizationId))
            throw WorkspaceUsage("Select an accessible organization with --organization <id>.");

        if (action == "list")
        {
            var items = await _apiClient.GetWorkspacesAsync(organizationId, ct);
            return Output(items, list =>
            {
                foreach (var item in list) WriteWorkspace(item);
                if (list.Count == 0) _console.WriteLine("No development workspaces.");
            });
        }
        if (action == "selections")
            return Output(
                await _apiClient.GetSelectionsAsync(organizationId, GetOption(args, "--workspace"), ct),
                WriteWorkspaceSelections);
        if (action == "create")
        {
            if (!int.TryParse(Required("--volume-gb"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size is < 10 or > 10_000)
                throw WorkspaceUsage("--volume-gb must be between 10 and 10000.");
            var name = Required("--name").Trim();
            var explicitKey = GetOption(args, "--idempotency-key");
            var key = explicitKey ?? DefaultWorkspaceCreateKey(organizationId, name);
            Note(explicitKey is null
                ? $"Create idempotency key: {key} (derived from organization and name). Re-running the same create resumes this workspace."
                : $"Create idempotency key: {key}. Reuse this key when retrying the same request.");
            var keepRunning = args.Contains("--keep-running", StringComparer.Ordinal);
            DevelopmentWorkspaceResponse created;
            try
            {
                created = await _apiClient.CreateWorkspaceAsync(new CreateDevelopmentWorkspaceRequest(
                    organizationId, name, Required("--image"), Required("--profile"), Required("--plan"), size, key,
                    IdleTimeoutOption("--idle-timeout"), keepRunning), ct);
            }
            catch (ApiException error) when (error.Code == "workspace_idempotency_conflict")
            {
                throw WorkspaceUsage(explicitKey is null
                    ? $"Workspace '{name}' already exists with a different image, profile, plan, volume size or idle setting. "
                        + "Inspect it with 'municode workspaces list', change it with resize-next-start or idle-timeout, or choose another --name."
                    : "This --idempotency-key was already used with a different request. Repeat the original options or use a new key.");
            }
            Output(created, WriteWorkspace);
            Note("Workspace created stopped; start is explicit.");
            if (created.KeepRunning) Note(KeepRunningCostNote(created));
            return CliExitCodes.Success;
        }

        var id = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("--", StringComparison.Ordinal))
            throw WorkspaceUsage($"Usage: municode workspaces {action} <workspace-id> [options]");
        if (action == "delete")
        {
            Note($"IRREVERSIBLE deletion requested for workspace {id}. Its primary retained volume and any remaining local VM data will be permanently lost.");
            return Output(await _apiClient.DeleteWorkspaceAsync(id, organizationId, ct), WriteWorkspace);
        }
        if (action is "vms" or "vm-status")
        {
            var vms = await _apiClient.GetWorkspaceVmsAsync(id, organizationId, ct);
            if (action == "vm-status") vms = vms.Where(x => x.Id == Required("--vm")).ToArray();
            if (vms.Count == 0 && action == "vm-status") throw WorkspaceUsage("VM not found in selected workspace.");
            return Output(vms, list => { foreach (var vm in list) WriteWorkspaceVm(vm); });
        }
        if (action == "open")
        {
            var current = await _apiClient.GetWorkspaceAsync(id, organizationId, ct);
            var vmId = GetOption(args, "--vm");
            var vm = (current.Vms ?? []).FirstOrDefault(x => vmId is null ? x.Kind == "primary" : x.Id == vmId)
                ?? throw WorkspaceUsage(vmId is null ? "Workspace has no primary VM." : "VM not found in selected workspace.");
            if (vm.Phase != "ready" || vm.DesiredState != "running" || vm.ErrorCode is not null)
                throw WorkspaceUsage($"VM {vm.Id} is {vm.DesiredState}/{vm.Phase}"
                    + (vm.ErrorCode is null ? "" : $" ({vm.ErrorCode})")
                    + $"; open requires a ready running VM. Start it with 'municode workspaces "
                    + (vm.Kind == "primary" ? $"start {id}'" : $"vm-start {id} --vm {vm.Id} --generation {vm.Generation}'")
                    + " and check status. Open never starts compute.");
            DevelopmentWorkspaceGatewayLaunchResponse launch;
            try
            {
                launch = await _apiClient.LaunchGatewayAsync(id, vm.Id, organizationId, vm.Generation, ct);
            }
            catch (ApiException error) when (error.StatusCode is 409 or 503)
            {
                throw WorkspaceUsage(error.StatusCode == 503
                    ? "The workspace gateway is not configured on this Municode server. Ask your operator to enable it."
                    : $"VM {vm.Id} generation {vm.Generation} is not ready for T3 access or you lack write access. Refresh status and retry.");
            }
            if (!Uri.TryCreate(launch.Url, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0)
                throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                    "The server returned an unsafe launch URL; an HTTPS URL without embedded credentials is required.");
            Output(launch, value =>
            {
                _console.WriteLine($"T3 on VM {vm.Id} generation {vm.Generation} ({vm.Kind}). Single-use link, expires {value.ExpiresAt:O}:");
                _console.WriteLine(value.Url);
            });
            if (!args.Contains("--no-browser", StringComparer.Ordinal) && !TryOpenBrowser(launch.Url))
                Note("Could not open a browser; open the link above before it expires.");
            Note("Do not share this link. Run open again for a new session after it expires.");
            return CliExitCodes.Success;
        }
        if (action.StartsWith("task-", StringComparison.Ordinal))
        {
            var complete = action == "task-complete";
            var body = complete
                ? (object)new CompleteDevelopmentWorkspaceTaskRequest(Generation(), args.Contains("--cancelled"))
                : new DevelopmentWorkspaceGenerationRequest(Generation());
            var lease = await _apiClient.WorkspaceTaskAsync(
                id, Required("--vm"), organizationId, Required("--task"), complete, body, ct);
            return Output(lease, value => _console.WriteLine(
                $"Task {value.TaskId}: {value.Status}; VM {value.VmId}, generation {value.Generation}; lease expires {value.ExpiresAt:O}"));
        }
        if (action == "launch")
        {
            Acknowledge("--push-authorized");
            Acknowledge("--acknowledge-ignored-data-loss");
            var request = new LaunchEphemeralDevelopmentWorkspaceVmRequest(
                Required("--image"), Required("--profile"), Required("--plan"), Required("--repository"),
                Required("--base-ref"), Required("--base-commit"), true, true,
                GetOption(args, "--idempotency-key") ?? Guid.NewGuid().ToString("N"),
                GetOption(args, "--repository-credential-ref"));
            Note($"Launch idempotency key: {request.IdempotencyKey}. Reuse this key when retrying the same request.");
            Output(await _apiClient.WorkspaceOperationAsync(id, organizationId, "vms/ephemeral", request, ct), WriteWorkspace);
            Note("Git-only root: ignored/untracked data is not retained. Launch does not push, open a PR, or merge.");
            return CliExitCodes.Success;
        }
        if (action.StartsWith("vm-", StringComparison.Ordinal))
        {
            var vm = Required("--vm");
            var operation = action["vm-".Length..];
            if (operation == "start" && args.Any(x => x is "--base-commit" or "--base-ref" or "--repository"))
                throw WorkspaceUsage("vm-start uses the recorded verified saved Git source; repository/base overrides are not supported.");
            if (operation == "start")
                Note("Stopped Git-only VMs recover their verified saved remote HEAD, not the original base. No commit or push is performed.");
            object body;
            if (operation == "discard")
            {
                Acknowledge("--discard-local-data");
                Acknowledge("--acknowledge-ignored-data-loss");
                body = new DevelopmentWorkspaceDiscardRequest(Generation(), true, true);
                Note($"IRREVERSIBLE discard requested for VM {vm}, generation {Generation()}. Local ignored/untracked/unpushed data will be lost.");
            }
            else
            {
                body = operation == "resize-next-start"
                    ? Resize()
                    : new DevelopmentWorkspaceGenerationRequest(Generation());
            }
            return Output(await _apiClient.WorkspaceOperationAsync(id, organizationId,
                $"vms/{Uri.EscapeDataString(vm)}/{(operation == "resize-next-start" ? "resize" : operation)}",
                body, ct), WriteWorkspace);
        }
        if (action == "idle-timeout")
        {
            var minutes = IdleTimeoutOption("--minutes");
            var useDefault = args.Contains("--default", StringComparer.Ordinal);
            var keepRunning = args.Contains("--keep-running", StringComparer.Ordinal);
            if ((minutes is not null ? 1 : 0) + (useDefault ? 1 : 0) + (keepRunning ? 1 : 0) != 1)
                throw WorkspaceUsage("Choose exactly one of --minutes <1-10080>, --default or --keep-running.");
            var request = keepRunning
                ? new UpdateDevelopmentWorkspaceIdleTimeoutRequest(
                    (await _apiClient.GetWorkspaceAsync(id, organizationId, ct)).IdleTimeoutMinutes, true)
                : new UpdateDevelopmentWorkspaceIdleTimeoutRequest(minutes);
            var updated = await _apiClient.UpdateIdleTimeoutAsync(id, organizationId, request, ct);
            Output(updated, WriteWorkspace);
            if (updated.KeepRunning) Note(KeepRunningCostNote(updated));
            return CliExitCodes.Success;
        }
        if (action == "stop-all")
        {
            var current = await _apiClient.GetWorkspaceAsync(id, organizationId, ct);
            Note("Stopping the following VMs; Git-save guards apply independently and may block stopping:");
            if (!json) foreach (var vm in current.Vms ?? []) WriteWorkspaceVm(vm);
        }
        var workspace = action == "status"
            ? await _apiClient.GetWorkspaceAsync(id, organizationId, ct)
            : action == "resize-next-start"
                ? await _apiClient.WorkspaceOperationAsync(id, organizationId, "resize", Resize(), ct)
                : await _apiClient.MutateWorkspaceAsync(id, organizationId, action, ct);
        return Output(workspace, WriteWorkspace);
    }

    private static CliCommandException WorkspaceUsage(string message) =>
        new(CliExitCodes.ValidationError, message);

    internal static string DefaultWorkspaceCreateKey(string organizationId, string name) =>
        "cli-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{organizationId}\n{name}")))
            .ToLowerInvariant()[..40];

    private static void ValidateWorkspaceDeletion(string[] args)
    {
        const string confirmation = "--confirm-permanent-deletion";
        const string usage =
            "Usage: municode workspaces delete <workspace-id> --confirm-permanent-deletion [--organization <id>]";
        if (args.ElementAtOrDefault(1) is not { Length: > 0 } id
            || string.IsNullOrWhiteSpace(id) || id.StartsWith('-'))
            throw WorkspaceUsage(usage);
        var confirmed = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] == confirmation && !confirmed) confirmed = true;
            else if (args[index] == "--organization" && index + 1 < args.Length && !args[index + 1].StartsWith('-')) index++;
            else if (args[index] != "--json" && !args[index].StartsWith("--organization=", StringComparison.Ordinal))
                throw WorkspaceUsage(usage);
        }
        if (!confirmed) throw WorkspaceUsage(usage);
    }

    private void WriteWorkspaceSelections(WorkspaceSelections selections)
    {
        if (!selections.Available)
        {
            _console.WriteLine($"Workspace creation is unavailable: {selections.UnavailableReason ?? "no eligible image, profile and plan."}");
            return;
        }
        foreach (var option in selections.Options)
            _console.WriteLine($"--image {option.ImageId} --profile {option.Profile} --plan {option.PlanId}  "
                + $"# {option.ImageName}; {option.PlanName} on {option.ProviderAccountName} {option.Region}/{option.Sku}"
                + (option.Agents is { Count: > 0 } agents ? "; agents: " + string.Join(", ", agents.Select(x => x.Id)) : ""));
        if (selections.Options.Count == 0) _console.WriteLine("No eligible image, profile and plan combinations.");
    }

    internal static string KeepRunningCostNote(DevelopmentWorkspaceResponse workspace)
    {
        var primary = (workspace.Vms ?? []).FirstOrDefault(x => x.Kind == "primary");
        var estimate = primary is null ? ""
            : $" The primary machine at its current size is estimated at {primary.ComputeHourlyRate * 720m:F2} {workspace.Currency}/month.";
        return "Keep running is on: machines in this dev environment are never stopped for inactivity. "
            + "Compute is billed for every running machine until you stop it with 'municode workspaces stop' or 'stop-all'."
            + estimate + " Cost estimates are informational and never block a start.";
    }

    private void WriteWorkspace(DevelopmentWorkspaceResponse workspace)
    {
        _console.WriteLine($"Workspace: {workspace.Name} ({workspace.Id})");
        _console.WriteLine($"Workspace phase: {workspace.Phase}");
        if (!string.IsNullOrWhiteSpace(workspace.ErrorCode) || !string.IsNullOrWhiteSpace(workspace.ErrorMessage))
            _console.WriteLine($"Blocked: {string.Join(": ", new[] { workspace.ErrorCode, workspace.ErrorMessage }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
        switch (workspace.Phase)
        {
            case "deleting" when workspace.VolumeStatus == "deleting":
                _console.WriteLine("Permanent deletion is in progress. Use workspaces status to check again.");
                return;
            case "delete_failed":
                _console.WriteLine("Permanent deletion failed. Resolve the reported error, then retry workspaces delete with --confirm-permanent-deletion.");
                return;
            case "deleted":
                _console.WriteLine("Workspace permanently deleted.");
                return;
        }
        _console.WriteLine(workspace.KeepRunning
            ? "Idle stop: off (Keep running); running machines are billed until you stop them."
            : workspace.EffectiveIdleTimeoutMinutes is { } timeout
                ? $"Idle stop: after {timeout} minutes without activity ({(workspace.IdleTimeoutMinutes is null ? "server default" : "workspace setting")})."
                : "Idle stop: timeout unavailable.");
        _console.WriteLine($"Primary retained storage: {workspace.VolumeSizeGb} GB; {workspace.VolumeStatus ?? "status unavailable"}; storage charges continue while stopped.");
        _console.WriteLine($"Estimated storage: {workspace.EstimatedMonthlyStorage:F2} {workspace.Currency}/month; rates {(workspace.PricingUpdatedAt?.ToString("O") ?? "timestamp unavailable")}");
        if (workspace.StorageLayout == 2)
            _console.WriteLine($"Checkouts: on the VM disk; last saved to retained storage {workspace.LastCheckpointAt?.ToString("O") ?? "never"} ({workspace.CheckpointStatus ?? "status unavailable"}).");
        _console.WriteLine(workspace.LastBackupAt is { } backup
            ? $"Off-host backup: last verified {backup:O} ({workspace.BackupStatus ?? "status unavailable"})."
            : $"Off-host backup: none verified ({workspace.BackupStatus ?? "status unavailable"}).");
        if (workspace.Agents is { Count: > 0 } agents)
            _console.WriteLine("Agents: " + string.Join(", ", agents.Select(x => $"{x.Name} {x.Version}")));
        foreach (var vm in workspace.Vms ?? []) WriteWorkspaceVm(vm);
    }

    private void WriteWorkspaceVm(DevelopmentWorkspaceVmResponse vm)
    {
        _console.WriteLine($"VM {vm.Id}: {vm.Kind}; {vm.DesiredState}/{vm.Phase}; generation={vm.Generation}; {vm.Profile}/{vm.Sku}/{vm.Region}");
        _console.WriteLine(vm.Kind == "primary"
            ? "Data: retained primary volume."
            : "Data: independent Git-only root; ignored/untracked/unpushed data can be lost. No primary volume access.");
        _console.WriteLine($"Readiness stage: {vm.Phase}; estimated compute {vm.EstimatedMonthlyCompute:F2}/month "
            + $"({vm.ComputeHourlyRate:F4}/hour); reserved={vm.ComputeReserved}");
        if (vm.ShutdownDeadline is not null) _console.WriteLine($"Idle stop deadline: {vm.ShutdownDeadline:O}");
        if (vm.LastActivityAt is not null) _console.WriteLine($"Last activity: {vm.LastActivityAt:O}");
        if (vm.NextProfile is not null)
            _console.WriteLine($"Next start: {vm.NextProfile}/{vm.NextPlanId}/{vm.NextImageId}; no live CPU/RAM resize.");
        if (vm.StartAfterStop) _console.WriteLine("Authorized start queued after the current generation is safely stopped.");
        if (vm.Repository is not null)
            _console.WriteLine($"Git: {vm.Repository} {vm.BaseRef}@{vm.BaseCommit}; branch={vm.WorkBranch}; "
                + $"save={vm.GitStatus}; remote={vm.VerifiedRemoteRef}@{vm.VerifiedRemoteCommit}");
        foreach (var lease in vm.TaskLeases ?? [])
            _console.WriteLine($"Task {lease.TaskId}: {lease.Status}; generation {lease.Generation}; lease expires {lease.ExpiresAt:O}");
        if (vm.TaskLeases is null || vm.TaskLeases.Count == 0) _console.WriteLine("No task leases reported.");
        if (vm.Phase == "awaiting_git_save")
            _console.WriteLine("Awaiting Git save: resolve the reported remote/authentication/conflict guard, then retry stop; "
                + "use explicit generation-bound discard only if local data loss is intended.");
        if (vm.ErrorCode is not null) _console.WriteLine($"Blocked: {vm.ErrorCode}: {vm.ErrorMessage}");
    }

    private void PrintWorkspaceHelp()
    {
        _console.WriteLine("Create, inspect, and control Municode development workspaces");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  municode workspaces list [--organization <id>]");
        _console.WriteLine("  municode workspaces selections [--workspace <id>] [--organization <id>]");
        _console.WriteLine("  municode workspaces create --name <name> --image <id> --profile Standard|Heavy --plan <id> --volume-gb <10-10000> "
            + "[--idle-timeout <1-10080> | --keep-running] [--idempotency-key <key>] [--organization <id>]");
        _console.WriteLine("  municode workspaces status <workspace-id> [--organization <id>]");
        _console.WriteLine("  municode workspaces open <workspace-id> [--vm <id>] [--no-browser]");
        _console.WriteLine("  municode workspaces delete <workspace-id> --confirm-permanent-deletion [--organization <id>]");
        _console.WriteLine("  municode workspaces <start|stop|cancel-idle-stop> <workspace-id> [--organization <id>]");
        _console.WriteLine("  municode workspaces idle-timeout <workspace-id> --minutes <1-10080>|--default|--keep-running");
        _console.WriteLine("  municode workspaces <vms|stop-all> <workspace-id> [--organization <id>]");
        _console.WriteLine("  municode workspaces launch <workspace-id> --image <id> --profile Standard|Heavy --plan <id> "
            + "--repository <url> --base-ref <ref> --base-commit <sha> --push-authorized --acknowledge-ignored-data-loss "
            + "[--idempotency-key <key>] [--repository-credential-ref <ref>]");
        _console.WriteLine("  municode workspaces vm-status <workspace-id> --vm <id>");
        _console.WriteLine("  municode workspaces <vm-start|vm-stop|vm-cancel-idle-stop> <workspace-id> --vm <id> --generation <n>");
        _console.WriteLine("  municode workspaces <resize-next-start|vm-resize-next-start> <workspace-id> [--vm <id>] "
            + "--generation <n> --image <id> --profile Standard|Heavy --plan <id>");
        _console.WriteLine("  municode workspaces vm-discard <workspace-id> --vm <id> --generation <n> "
            + "--discard-local-data --acknowledge-ignored-data-loss");
        _console.WriteLine("  municode workspaces <task-renew|task-complete> <workspace-id> --vm <id> --generation <n> --task <id> [--cancelled]");
        _console.WriteLine();
        _console.WriteLine("All workspace commands accept --organization <id> and --json. --organization is required when multiple organizations are accessible.");
        _console.WriteLine("Create never starts compute. Open mints a short-lived HTTPS T3 link and never starts compute.");
        _console.WriteLine("Ephemeral roots are Git-only; ignored, untracked, or unpushed data is not retained.");
        _console.WriteLine("Permanent deletion and VM discard require the separate explicit irreversible-data confirmations shown above.");
    }
}
