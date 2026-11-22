using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Providers.DigitalOcean;
using Workspaces.Common.Providers.Hetzner;
using Workspaces.Common.Providers.Vultr;
using Workspaces.Common.Services;

namespace Workspaces.Common.Providers;

// A new adapter instance captures one credential generation. Authorization is added
// per request by the adapters, never to shared transport default headers.
public class ProviderAccountClientFactory(
    WorkspaceDbContext db, ISecretStore secrets, IHttpClientFactory http,
    IOptions<HetznerOptions> hetzner, IOptions<DigitalOceanOptions> digitalOcean,
    IOptions<VultrOptions> vultr)
{
    public const string HttpClientName = "provider-accounts";

    public virtual async Task<IDevelopmentWorkspaceProvider> CreateAsync(string organizationId, string providerAccountId,
        CancellationToken ct, bool allowDisabled = true)
    {
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == providerAccountId && x.OrganizationId == organizationId, ct)
            ?? throw new ProviderCatalogException("provider_not_found", "Provider account was not found in this organization.");
        if (!allowDisabled && !account.Enabled)
            throw new ProviderCatalogException("provider_disabled", "This provider account is disabled for new allocation.");
        if (account.CredentialStoreProvider != OpenBaoSecretStore.ProviderName || string.IsNullOrWhiteSpace(account.CredentialPath))
            throw new SecretStoreUnavailableException("Provider account credentials are unavailable.");
        var credential = await secrets.GetAsync(new StoredSecretReference(account.CredentialStoreProvider,
            account.CredentialPath, account.CredentialVersion), ct);
        if (string.IsNullOrWhiteSpace(credential)) throw new SecretStoreUnavailableException("Provider account credentials are empty.");
        return CreateCandidate(account, credential);
    }

    public virtual IDevelopmentWorkspaceProvider CreateCandidate(ProviderAccount account, string credential)
    {
        var client = http.CreateClient(HttpClientName);
        return account.ProviderType switch
        {
            CloudProvider.Hetzner => new HetznerClient(client, new HetznerOptions
                { ApiToken = credential, BaseUrl = hetzner.Value.BaseUrl }),
            CloudProvider.DigitalOcean => new DigitalOceanClient(client, new DigitalOceanOptions
                { ApiToken = credential, BaseUrl = digitalOcean.Value.BaseUrl, ProjectId = account.ProjectId }),
            CloudProvider.Vultr => new VultrClient(client, new VultrOptions
                { ApiKey = credential, BaseUrl = vultr.Value.BaseUrl, DefaultOsId = account.DefaultOsId }),
            _ => throw new ProviderCatalogException("provider_type_unsupported", "This workspace provider type is not supported.")
        };
    }
}
