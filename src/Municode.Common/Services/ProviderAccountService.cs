using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Municode.Common.Contracts;
using Municode.Common.Data;
using Municode.Common.Models;
using Municode.Common.Providers;

namespace Municode.Common.Services;

public sealed class ProviderAccountService(WorkspaceDbContext db, ISecretStore secrets,
    ProviderAccountClientFactory clients, IIdGenerator ids, TimeProvider clock, IOptions<OpenBaoSecretStoreOptions> options)
{
    /// <param name="id">Seed registration only: a deterministic account ID that the seeded policy references.</param>
    public async Task<ProviderAccount> CreateAsync(CreateProviderRequest request, CancellationToken ct, string? id = null)
    {
        ValidateName(request.Name);
        if (!TryParseType(request.ProviderType, out var type))
            throw new ProviderCatalogException("provider_type_unsupported", "Select Hetzner, DigitalOcean or Vultr.");
        if (request.DefaultOsId is <= 0 || request.ProjectId is { Length: > 128 } || request.ProjectId?.Any(char.IsControl) == true)
            throw new ProviderCatalogException("provider_settings_invalid", "The provider account settings are invalid.");
        var now = clock.GetUtcNow();
        var account = new ProviderAccount
        {
            Id = id ?? ids.NewId("provider"), OrganizationId = request.OrganizationId, Name = request.Name.Trim(), ProviderType = type,
            ProjectId = request.ProjectId, DefaultOsId = request.DefaultOsId ?? (type == CloudProvider.Vultr ? 2284 : null),
            CreatedAt = now, UpdatedAt = now
        };
        var reference = await WriteCandidateAsync(account, request.Credential, ct);
        var candidate = clients.CreateCandidate(account, request.Credential);
        await ProbeAsync(candidate, ct);
        await RejectKnownScopeAliasAsync(account, candidate, ct);
        AssignCredential(account, reference);
        account.ValidationStatus = "valid"; account.LastValidatedAt = now;
        db.ProviderAccounts.Add(account);
        return account;
    }

    public void Update(ProviderAccount account, UpdateProviderRequest request)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        if (request.Name is not null) { ValidateName(request.Name); account.Name = request.Name.Trim(); }
        account.Enabled = request.Enabled ?? account.Enabled;
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
    }

    public async Task ReplaceCredentialsAsync(ProviderAccount account, ReplaceProviderCredentialRequest request, CancellationToken ct)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var reference = await WriteCandidateAsync(account, request.Credential, ct);
        var generation = account.CredentialGeneration;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({DevelopmentWorkspaceService.AdmissionLockId})", ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var candidate = clients.CreateCandidate(account, request.Credential);
        await ProbeAsync(candidate, ct);
        if (await db.DevelopmentWorkspaceVms.AnyAsync(x => x.ProviderAccountId == account.Id
                && (x.ComputeReserved || x.ProviderServerId != null || x.ProviderIpId != null || x.ProviderFirewallId != null), ct)
            || await db.DevelopmentWorkspaceOperations.AnyAsync(x => x.ProviderAccountId == account.Id
                && (x.Status == "pending" || x.Status == "running"), ct))
            throw new ProviderCatalogException("provider_in_use", "Stop active VMs and wait for workspace operations to finish before rotating credentials.");
        await VerifyExistingScopeAsync(account, candidate, ct);
        await RejectKnownScopeAliasAsync(account, candidate, ct);
        AssignCredential(account, reference);
        account.CredentialGeneration = generation;
        account.ValidationStatus = "valid"; account.LastValidatedAt = clock.GetUtcNow();
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ProviderValidationResponse> ValidateAsync(ProviderAccount account, CancellationToken ct)
    {
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        try
        {
            var client = await clients.CreateAsync(account.OrganizationId, account.Id, ct);
            await ProbeAsync(client, ct);
            await VerifyExistingScopeAsync(account, client, ct);
            account.ValidationStatus = "valid";
        }
        catch (ProviderCatalogException ex) when (ex.Code == "provider_scope_unverified") { account.ValidationStatus = "scope_unverified"; }
        catch (HttpRequestException) { account.ValidationStatus = "invalid_or_unavailable"; }
        catch (SecretStoreUnavailableException) { account.ValidationStatus = "credentials_unavailable"; }
        account.LastValidatedAt = clock.GetUtcNow(); account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        return new ProviderValidationResponse(account.ValidationStatus, account.LastValidatedAt.Value);
    }

    public async Task DeleteAsync(ProviderAccount account, CancellationToken ct)
    {
        await EnsureUnusedAsync(account, ct);
        account.Enabled = false; account.ValidationStatus = "deleting"; account.Revision++;
        await db.SaveChangesAsync(ct);
        // The disabled deletion intent is committed before touching OpenBao, so a crash
        // cannot leave an enabled account referring to removed credentials.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        await EnsureUnusedAsync(account, ct);
        var cleanup = await db.ProviderCredentialCleanups.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        foreach (var item in cleanup) await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
        if (!cleanup.Any(x => x.Path == account.CredentialPath))
            await secrets.DeleteAsync(new StoredSecretReference(account.CredentialStoreProvider, account.CredentialPath, account.CredentialVersion), ct);
        db.ProviderCredentialCleanups.RemoveRange(cleanup);
        db.ProviderAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Filter dates in memory as SQLite is used by development installations/tests.
        var pending = (await db.ProviderCredentialCleanups.AsNoTracking().ToListAsync(ct)).Where(x => x.NotBefore <= now).Take(50);
        foreach (var item in pending)
        {
            if (await db.ProviderAccounts.AnyAsync(x => x.CredentialPath == item.Path, ct)) continue;
            await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
            db.ProviderCredentialCleanups.Remove(item);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<StoredSecretReference> WriteCandidateAsync(ProviderAccount account, string credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 8192 || credential.Any(char.IsControl))
            throw new ProviderCatalogException("provider_credential_invalid", "Enter a provider API credential without control characters.");
        var generation = Guid.NewGuid().ToString("N");
        var path = OpenBaoSecretStore.ProviderPathFor(options.Value.EnvironmentPrefix, account.OrganizationId, account.Id, generation);
        db.ProviderCredentialCleanups.Add(new ProviderCredentialCleanup
        {
            Id = ids.NewId("credential_cleanup"), OrganizationId = account.OrganizationId, ProviderAccountId = account.Id,
            Path = path, NotBefore = clock.GetUtcNow().AddDays(1)
        });
        // Write cleanup intent first. It is safe to delete an absent generation.
        await db.SaveChangesAsync(ct);
        var reference = await secrets.PutAsync(new SecretStoreWriteRequest(account.OrganizationId, credential)
            { ProviderAccountId = account.Id, CredentialGeneration = generation }, ct);
        account.CredentialGeneration = generation;
        return reference;
    }

    private static void AssignCredential(ProviderAccount account, StoredSecretReference reference)
    {
        account.CredentialStoreProvider = reference.Provider; account.CredentialPath = reference.Path; account.CredentialVersion = reference.Version;
    }

    private static Task ProbeAsync(IDevelopmentWorkspaceProvider client, CancellationToken ct) =>
        client.FindWorkspaceServersAsync(new Dictionary<string, string>
        {
            ["managed-by"] = WorkspaceOwnership.Manager,
            ["organization"] = "credential-probe",
            ["workspace"] = Guid.NewGuid().ToString("N"),
            ["kind"] = "primary"
        }, ct);

    internal static Dictionary<string, string> VolumeLabels(DevelopmentWorkspace workspace) => new()
    {
        ["managed-by"] = WorkspaceOwnership.Manager,
        ["organization"] = workspace.OrganizationId,
        ["workspace"] = workspace.Id,
        ["kind"] = "primary"
    };

    private async Task VerifyExistingScopeAsync(ProviderAccount account, IDevelopmentWorkspaceProvider candidate, CancellationToken ct)
    {
        var workspaceVolumes = await db.DevelopmentWorkspaces.AsNoTracking()
            .Where(x => x.ProviderAccountId == account.Id && x.OrganizationId == account.OrganizationId && x.ProviderVolumeId != null)
            .ToListAsync(ct);
        foreach (var workspace in workspaceVolumes)
        {
            var observed = await candidate.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId!, ct);
            if (observed is null || observed.Id != workspace.ProviderVolumeId || observed.Region != workspace.Region
                || !WorkspaceOwnership.Matches(observed.Labels, VolumeLabels(workspace)))
                throw ScopeUnverified();
        }
    }

    private async Task RejectKnownScopeAliasAsync(ProviderAccount account, IDevelopmentWorkspaceProvider candidate, CancellationToken ct)
    {
        var otherVolumes = await db.DevelopmentWorkspaces.AsNoTracking().Where(x => x.ProviderVolumeId != null
            && x.ProviderAccountId != account.Id && db.ProviderAccounts.Any(p => p.Id == x.ProviderAccountId
                && p.ProviderType == account.ProviderType)).ToListAsync(ct);
        foreach (var workspace in otherVolumes)
        {
            var observed = await candidate.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId!, ct);
            if (observed is not null && WorkspaceOwnership.Matches(observed.Labels, VolumeLabels(workspace)))
                throw new ProviderCatalogException("provider_scope_already_registered", "This cloud scope already contains resources registered through another provider entry. Use that entry instead.");
        }
    }

    private async Task EnsureUnusedAsync(ProviderAccount account, CancellationToken ct)
    {
        if (await db.ProviderPlans.AnyAsync(x => x.ProviderAccountId == account.Id, ct)
            || await db.DevelopmentWorkspaces.AnyAsync(x => x.ProviderAccountId == account.Id, ct)
            || await db.DevelopmentWorkspaceVms.AnyAsync(x => x.ProviderAccountId == account.Id, ct))
            throw new ProviderCatalogException("provider_in_use", "A plan or workspace references this provider account. Disable it instead.");
    }

    public static bool TryParseType(string value, out CloudProvider type)
    {
        type = value?.ToLowerInvariant() switch { "hetzner" => CloudProvider.Hetzner, "digital-ocean" or "digitalocean" => CloudProvider.DigitalOcean, "vultr" => CloudProvider.Vultr, _ => (CloudProvider)(-1) };
        return Enum.IsDefined(type);
    }
    public static string WireType(CloudProvider type) => type switch { CloudProvider.Hetzner => "hetzner", CloudProvider.DigitalOcean => "digital-ocean", CloudProvider.Vultr => "vultr", _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static ProviderCatalogException ScopeUnverified() => new("provider_scope_unverified", "Credentials could not be verified against all existing resources. Restore access to the original cloud scope before replacing credentials.");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ProviderCatalogException("provider_name_invalid", "Use a provider name of 1 to 128 characters without control characters.");
    }
}
