namespace Municode.Common.Services;

/// <summary>
/// Control-plane host addresses that workspace firewalls admit for SSH. Shared with the worker section so the API
/// can report the missing prerequisite before any workspace compute is requested.
/// </summary>
public sealed class DevelopmentWorkspaceManagementOptions
{
    public const string SectionName = "DevelopmentWorkspaceWorker";
    public string[] ManagementAddresses { get; set; } = [];
}
