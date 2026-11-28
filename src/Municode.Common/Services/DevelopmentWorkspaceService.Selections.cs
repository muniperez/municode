using Microsoft.EntityFrameworkCore;
using Municode.Common.Contracts;
using Municode.Common.Models;
using Municode.Common.Providers;

namespace Municode.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    public async Task<WorkspaceSelections> GetSelectionsAsync(
        string organizationId, string? workspaceId, CancellationToken ct)
    {
        ValidateText(organizationId, 128);
        DevelopmentWorkspace? workspace = null;
        if (workspaceId is not null)
        {
            ValidateText(workspaceId, 128);
            workspace = await db.DevelopmentWorkspaces.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == workspaceId, ct)
                ?? throw Invalid("workspace_not_found", "Workspace was not found.");
        }

        try { ValidatePolicy(); ValidateManagementAddresses(); }
        catch (DevelopmentWorkspaceException error)
            when (error.Code is "workspace_policy_invalid" or "workspace_management_addresses_missing")
        {
            return new(false, error.Message, []);
        }

        // Project only public catalog metadata; discovery never resolves provider credentials.
        var plans = await db.ProviderPlans.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ProviderAccount.OrganizationId == organizationId)
            .Select(x => new SelectionPlan(x.Id, x.Name, x.OrganizationId, x.ProviderAccountId,
                x.Region, x.Sku, x.BuildPlatform, x.Enabled, x.ProviderAccount.Name,
                x.ProviderAccount.OrganizationId, x.ProviderAccount.Enabled, x.ProviderAccount.ProviderType))
            .ToListAsync(ct);
        var selections = new List<WorkspaceSelectionOption>();
        foreach (var (imageId, image) in Policy.Images.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!ValidText(imageId, 128) || !EligibleImage(image, forNewWorkspace: workspace is null)) continue;
            foreach (var (profileId, profile) in Policy.Profiles.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!ValidText(profileId, 32)) continue;
                foreach (var plan in plans.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    if (!ValidText(plan.Id, 128) || !EligibleProfile(profile, plan.Id)
                        || !EligiblePlan(organizationId, image, profile, plan)
                        || workspace is not null && !MatchesPlacement(workspace, plan.ProviderAccountId, plan.Region))
                        continue;
                    selections.Add(new(imageId, string.IsNullOrWhiteSpace(image.DisplayName) ? imageId : image.DisplayName.Trim(),
                        profileId, plan.Id, plan.Name, plan.ProviderAccountName, plan.Region, plan.Sku,
                        profile.CpuCount, profile.MemoryGiB, profile.ComputeHourlyRate,
                        workspace is null && profile.DefaultPlanIdsForNewWorkspaces.Contains(plan.Id, StringComparer.Ordinal),
                        ImageAgents(image)));
                }
            }
        }
        var missing = selections.Count == 0
            ? await MissingConfigurationAsync(organizationId, plans, forNewWorkspace: workspace is null, ct)
            : null;
        return new(true, null, selections, Policy.Currency, Policy.VolumeMonthlyRatePerGb, missing);
    }

    // Explains the first missing prerequisite for an empty catalog. Only public catalog metadata is consulted.
    private async Task<string> MissingConfigurationAsync(string organizationId, IReadOnlyList<SelectionPlan> plans,
        bool forNewWorkspace, CancellationToken ct)
    {
        const string section = DevelopmentWorkspaceOptions.SectionName + ":";
        var accounts = await db.ProviderAccounts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Enabled)
            .Select(x => x.Id).ToListAsync(ct);
        if (accounts.Count == 0)
            return "Add an enabled cloud provider account for this organization under Providers.";
        var enabledPlans = plans.Where(x => x.Enabled && x.AccountEnabled && x.OrganizationId == organizationId
            && x.AccountOrganizationId == organizationId).ToList();
        if (enabledPlans.Count == 0)
            return "Create an enabled provider plan (region and server type) under Providers.";
        var images = Policy.Images.Where(x => ValidText(x.Key, 128) && EligibleImage(x.Value, forNewWorkspace)).ToList();
        if (images.Count == 0)
            return forNewWorkspace
                ? $"Ask an operator to register a runtime-qualified T3 Connect image under {section}Images in the deployment policy."
                : $"Ask an operator to register a runtime-qualified image under {section}Images in the deployment policy.";
        var ownImages = images.Where(x => accounts.Contains(x.Value.ProviderAccountId, StringComparer.Ordinal)).ToList();
        if (ownImages.Count == 0)
            return $"No qualified image belongs to this organization's provider accounts; ask an operator to set an image's ProviderAccountId under {section}Images.";
        if (!ownImages.Any(image => enabledPlans.Any(plan => plan.ProviderAccountId == image.Value.ProviderAccountId
                && plan.BuildPlatform == "linux/amd64" && image.Value.AllowedRegions.Contains(plan.Region, StringComparer.Ordinal))))
            return "No enabled linux/amd64 plan uses the same provider account and an allowed region as a qualified image; update the plan or the image's AllowedRegions.";
        if (Policy.Profiles.Count == 0)
            return $"Ask an operator to define a workspace size under {section}Profiles in the deployment policy.";
        if (!forNewWorkspace)
            return $"No workspace size allows an enabled plan in this workspace's provider account and region; ask an operator to update {section}Profiles.";
        return $"No workspace size allows an enabled plan; ask an operator to add the plan's ID, server type and region to a profile's AllowedPlanIds, AllowedSkus and AllowedRegions under {section}Profiles.";
    }

    private bool EligibleImage(DevelopmentWorkspaceImage image, bool forNewWorkspace) =>
        image.Enabled && image.HasKnownSource() && image.RuntimeQualified && image.QualifiedAt is not null && image.QualifiedAt <= Now
        && Sha(image.ManifestSha256, 64) && !string.IsNullOrWhiteSpace(image.ProviderImageId)
        && image.Architecture == "linux/amd64" && image.HasSupportedT3Protocol()
        && (!forNewWorkspace || image.T3ConnectForNewWorkspaces);

    private static DevelopmentWorkspaceAgentResponse[]? ImageAgents(DevelopmentWorkspaceImage? image)
    {
        var agents = (image?.Agents ?? []).Where(x => ValidText(x.Id, 64) && ValidText(x.Name, 128) && ValidText(x.Version, 128))
            .Select(x => new DevelopmentWorkspaceAgentResponse(x.Id.Trim(), x.Name.Trim(), x.Version.Trim())).ToArray();
        return agents.Length == 0 ? null : agents;
    }

    private static bool EligibleProfile(DevelopmentWorkspaceProfile profile, string planId) =>
        profile.MemoryGiB is 8 or 16 && profile.CpuCount > 0 && profile.ComputeHourlyRate > 0
        && profile.AllowedPlanIds.Contains(planId, StringComparer.Ordinal);

    private static bool EligiblePlan(string organizationId, DevelopmentWorkspaceImage image,
        DevelopmentWorkspaceProfile profile, SelectionPlan plan) =>
        plan.OrganizationId == organizationId && plan.Enabled && plan.AccountEnabled
        && plan.ProviderType is CloudProvider.Hetzner or CloudProvider.DigitalOcean or CloudProvider.Vultr && plan.AccountOrganizationId == organizationId
        && plan.BuildPlatform == "linux/amd64" && image.ProviderAccountId == plan.ProviderAccountId
        && image.AllowedRegions.Contains(plan.Region, StringComparer.Ordinal)
        && profile.AllowedSkus.Contains(plan.Sku, StringComparer.Ordinal)
        && profile.AllowedRegions.Contains(plan.Region, StringComparer.Ordinal);

    private static bool MatchesPlacement(DevelopmentWorkspace workspace, string providerAccountId, string region) =>
        workspace.ProviderAccountId == providerAccountId && workspace.Region == region;

    private sealed record SelectionPlan(
        string Id, string Name, string OrganizationId, string ProviderAccountId, string Region, string Sku,
        string BuildPlatform, bool Enabled, string ProviderAccountName, string AccountOrganizationId,
        bool AccountEnabled, CloudProvider ProviderType);
}
