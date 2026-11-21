using System.Text;
using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Providers;

namespace Workspaces.Common.Services;

public sealed class ProviderCatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ProviderCatalogService(WorkspaceDbContext db, IIdGenerator ids, TimeProvider clock)
{
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ProviderCatalogException("plan_name_invalid", "Use a nonempty plan name of up to 128 characters without control characters.");
        try { return name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
        catch (ArgumentException) { throw new ProviderCatalogException("plan_name_invalid", "The plan name contains invalid Unicode."); }
    }

    public async Task<ProviderPlan> CreateAsync(CreateProviderPlanRequest request, CancellationToken ct)
    {
        var account = await LockAccountAsync(db, request.OrganizationId, request.ProviderAccountId, ct);
        if (!account.Enabled) throw new ProviderCatalogException("provider_disabled", "Enable the provider account before adding a plan.");
        var name = request.Name is null ? Guid.NewGuid().ToString() : request.Name.Trim();
        var key = NormalizeName(name);
        await CheckNameAsync(request.OrganizationId, key, null, ct);
        ValidatePlacement(request.Region, request.Sku, request.BuildPlatform, request.Image, account.ProviderType);
        var now = clock.GetUtcNow();
        var plan = new ProviderPlan
        {
            Id = ids.NewId("plan"), OrganizationId = request.OrganizationId, ProviderAccountId = account.Id,
            ProviderAccount = account, Name = name, NormalizedName = key, Region = request.Region.Trim(), Sku = request.Sku.Trim(),
            Image = string.IsNullOrWhiteSpace(request.Image) ? DefaultImage(account.ProviderType) : request.Image.Trim(),
            BuildPlatform = request.BuildPlatform, Enabled = request.Enabled, CreatedAt = now, UpdatedAt = now
        };
        db.ProviderPlans.Add(plan);
        db.ProviderPlanNames.Add(new ProviderPlanName { OrganizationId = plan.OrganizationId, NormalizedName = key, PlanId = plan.Id });
        return plan;
    }

    public async Task UpdateAsync(ProviderPlan plan, UpdateProviderPlanRequest request, CancellationToken ct)
    {
        if (plan.Revision != request.Revision) throw Stale();
        var region = request.Region?.Trim() ?? plan.Region;
        var sku = request.Sku?.Trim() ?? plan.Sku;
        var image = request.Image?.Trim() ?? plan.Image;
        var platform = request.BuildPlatform ?? plan.BuildPlatform;
        ValidatePlacement(region, sku, platform, image, plan.ProviderAccount.ProviderType);
        if (plan.ProvisioningLockedAt is not null && (region != plan.Region || sku != plan.Sku || image != plan.Image || platform != plan.BuildPlatform))
            throw new ProviderCatalogException("plan_mapping_locked", "This plan has been reserved for provisioning. Duplicate it to change its mapping.");
        if (request.Name is not null)
        {
            var key = NormalizeName(request.Name);
            if (key != plan.NormalizedName)
            {
                await CheckNameAsync(plan.OrganizationId, key, plan.Id, ct);
                var previous = await db.ProviderPlanNames.SingleAsync(x => x.OrganizationId == plan.OrganizationId && x.NormalizedName == plan.NormalizedName, ct);
                previous.Retired = true;
                var next = await db.ProviderPlanNames.SingleOrDefaultAsync(x => x.OrganizationId == plan.OrganizationId && x.NormalizedName == key, ct);
                if (next is null) db.ProviderPlanNames.Add(new ProviderPlanName { OrganizationId = plan.OrganizationId, NormalizedName = key, PlanId = plan.Id });
                else next.Retired = false;
                plan.NormalizedName = key;
            }
            plan.Name = request.Name.Trim();
        }
        plan.Region = region; plan.Sku = sku; plan.Image = image; plan.BuildPlatform = platform;
        plan.Enabled = request.Enabled ?? plan.Enabled;
        plan.UpdatedAt = clock.GetUtcNow(); plan.Revision++;
    }

    public async Task DeleteAsync(ProviderPlan plan, CancellationToken ct)
    {
        if (await db.DevelopmentWorkspaceVms.AnyAsync(x => x.PlanId == plan.Id, ct))
            throw new ProviderCatalogException("plan_in_use", "This plan is referenced by a workspace VM. Disable it instead.");
        foreach (var claim in await db.ProviderPlanNames.Where(x => x.OrganizationId == plan.OrganizationId && x.PlanId == plan.Id).ToListAsync(ct)) claim.Retired = true;
        db.ProviderPlans.Remove(plan);
    }

    public async Task<ProviderPlan> LockedPlanAsync(string organizationId, string id, CancellationToken ct)
    {
        IQueryable<ProviderPlan> query = db.ProviderPlans;
        if (db.Database.IsNpgsql()) query = db.ProviderPlans.FromSqlInterpolated($"SELECT * FROM provider_plans WHERE organization_id = {organizationId} AND id = {id} FOR UPDATE");
        var plan = await query.Include(x => x.ProviderAccount).SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct)
            ?? throw new ProviderCatalogException("plan_not_found", "Plan was not found.");
        // A selection lookup may already be tracking this row before waiting on its
        // lock. Refresh after acquisition so reservation cannot snapshot stale fields.
        if (db.Database.IsNpgsql()) await db.Entry(plan).ReloadAsync(ct);
        return plan;
    }

    public static async Task<ProviderAccount> LockAccountAsync(WorkspaceDbContext db, string organizationId, string id, CancellationToken ct)
    {
        IQueryable<ProviderAccount> query = db.ProviderAccounts;
        if (db.Database.IsNpgsql()) query = db.ProviderAccounts.FromSqlInterpolated(
            $"SELECT * FROM provider_accounts WHERE organization_id = {organizationId} AND id = {id} FOR UPDATE");
        var account = await query.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct)
            ?? throw new ProviderCatalogException("provider_not_found", "Provider account was not found in this organization.");
        await db.Entry(account).ReloadAsync(ct);
        return account;
    }

    private async Task CheckNameAsync(string organizationId, string key, string? currentId, CancellationToken ct)
    {
        if (await db.ProviderPlanNames.AnyAsync(x => x.OrganizationId == organizationId && x.NormalizedName == key && x.PlanId != currentId, ct))
            throw new ProviderCatalogException("plan_name_taken", "This plan name is already used or reserved in this organization.");
    }

    public static ProviderCatalogException Stale() => new("revision_conflict", "This item changed. Reload it before saving.");

    public static string? DefaultImage(CloudProvider type) => type switch
    {
        CloudProvider.Hetzner => "ubuntu-24.04", CloudProvider.DigitalOcean => "ubuntu-24-04-x64", _ => null
    };

    private static void ValidatePlacement(string region, string sku, string platform, string? image, CloudProvider provider)
    {
        if (provider == CloudProvider.Vultr && !string.IsNullOrWhiteSpace(image)
            && (!int.TryParse(image.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var osId) || osId <= 0))
            throw new ProviderCatalogException("plan_mapping_invalid", "Vultr plan OS overrides must be positive numeric OS IDs.");
        if (string.IsNullOrWhiteSpace(region) || region.Length > 128 || region.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(sku) || sku.Length > 128 || sku.Any(char.IsControl)
            || image is { Length: > 128 } || image?.Any(char.IsControl) == true
            || platform is not ("linux/amd64" or "linux/arm64"))
            throw new ProviderCatalogException("plan_mapping_invalid", "Specify a region, provider SKU, supported image and linux/amd64 or linux/arm64 platform.");
    }
}
