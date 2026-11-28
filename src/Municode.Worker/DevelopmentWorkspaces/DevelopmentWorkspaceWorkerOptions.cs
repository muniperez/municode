using System.Text.RegularExpressions;
using Municode.Common.Providers;
using Municode.Common.Services;

namespace Municode.Worker.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceWorkerOptions
{
    public const string SectionName = "DevelopmentWorkspaceWorker";
    public string ApiBaseUrl { get; set; } = "";
    public string[] ManagementAddresses { get; set; } = [];
    public string RelayBindAddress { get; set; } = "127.0.0.1";
    public string RelayLockFile { get; set; } = "";
    public int RelayMinimumPort { get; set; } = 42000;
    public int RelayMaximumPort { get; set; } = 42999;
    public int PollSeconds { get; set; } = 5;
    public int IdleShutdownDelaySeconds { get; set; } = 60;
    public int GatewayDrainSeconds { get; set; } = 75;
    public int SessionLifetimeSeconds { get; set; } = 1800;
    public string? BackupRecipient { get; set; }
    public string? BackupRecoveryId { get; set; }
    public string? BackupDestinationIdentity { get; set; }
    public int BackupRetentionDays { get; set; } = 7;
    public bool ObservedTerminalInputs { get; set; }
    // New primary volumes use storage layout 2 (VM-disk checkouts checkpointed to the volume).
    // Requires an image whose runtime supports it; existing workspaces keep their layout.
    public bool LocalCheckouts { get; set; }
    // With LocalCheckouts, also migrate existing layout 1 primaries to layout 2 on their next start.
    // Requires an image whose runtime supports migration; the layout is recorded only after a Ready bootstrap.
    public bool MigrateExistingCheckouts { get; set; }
    // Layout 2: how often a running primary checkpoints its checkouts to the retained volume.
    public int CheckpointIntervalSeconds { get; set; } = DefaultCheckpointIntervalSeconds;
    internal const int DefaultCheckpointIntervalSeconds = 300;
    public Dictionary<string, DevelopmentWorkspacePreviewService> PreviewServices { get; set; } = new()
    {
        ["connect"] = new() { Port = 774 }
    };

    internal TimeSpan GatewayDrain(DevelopmentWorkspaceGatewayOptions gateway) =>
        TimeSpan.FromSeconds(Math.Max(Math.Max(75, GatewayDrainSeconds),
            Math.Clamp(gateway.HttpTimeoutSeconds, 1, 300) + 10));

    internal bool HasManagementAddresses()
    {
        try { WorkspaceOwnership.RequireManagementAddresses(ManagementAddresses); return true; }
        catch (ArgumentException) { return false; }
    }

    internal void Validate(WorkspaceSshOptions ssh, DevelopmentWorkspaceGatewayOptions gateway)
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidOperationException("Workspace enrollment requires a Workspaces API HTTPS base URL.");
        if (!Path.IsPathFullyQualified(RelayLockFile) || !DevelopmentWorkspaceGatewayOptions.IsPrivateAddress(RelayBindAddress) ||
            RelayMinimumPort < Math.Max(1024, gateway.MinimumRelayPort) ||
            RelayMaximumPort > Math.Min(65535, gateway.MaximumRelayPort) || RelayMinimumPort > RelayMaximumPort)
            throw new InvalidOperationException("Workspace relays require an absolute lock path, private bind address and a port range within the gateway range.");
        if (CheckpointIntervalSeconds is < 60 or > 3600)
            throw new InvalidOperationException("Workspace checkpoint intervals must be between 60 and 3600 seconds.");
        foreach (var (service, preview) in PreviewServices)
        {
            if (!Regex.IsMatch(service, "^[a-z][a-z0-9-]{0,15}$") || service == "t3" ||
                !WorkspaceTunnelManager.IsPermittedRemotePort(service, preview.Port) || preview.Port == 3773 ||
                (preview.SupervisedUnit is { } unit &&
                 (service == "connect" || !Regex.IsMatch(unit, @"^municode-preview-[a-z0-9-]{1,24}\.service$"))))
                throw new InvalidOperationException("Workspace previews require a unique gateway service name, its permitted port and an optional registered preview unit.");
        }
        ssh.Validate();
    }
}

public sealed class DevelopmentWorkspacePreviewService
{
    public int Port { get; set; }
    public string? SupervisedUnit { get; set; }
}
