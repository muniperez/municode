using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Municode.Common.Contracts;
using Municode.Common.Data;
using Municode.Common.Services;

namespace Municode.Tests;

public sealed partial class DevelopmentWorkspaceApiTests
{
    [Fact]
    public async Task ProviderAndPlanOnboardingAreTenantScopedAndKeepCredentialsOutOfTheDatabase()
    {
        using var owner = Client(token);
        using var outsider = Client(otherToken);
        const string credential = "synthetic-provider-token";
        var request = new CreateProviderRequest("org", "Fixture provider", "hetzner", credential);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/providers", request)).StatusCode);
        Assert.Empty(factory.Secrets.Values);

        using var created = await owner.PostAsJsonAsync("/api/providers", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(credential, await created.Content.ReadAsStringAsync());
        var provider = (await created.Content.ReadFromJsonAsync<ProviderAccountResponse>())!;
        Assert.Equal("valid", provider.ValidationStatus);
        Assert.Equal(credential, Assert.Single(factory.Secrets.Values).Value);

        var planRequest = new CreateProviderPlanRequest("org", provider.Id, "nbg1", "cx-fixture", "Fixture size");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await outsider.PostAsJsonAsync("/api/plans", planRequest with { OrganizationId = "other" })).StatusCode);
        using var planCreated = await owner.PostAsJsonAsync("/api/plans", planRequest);
        Assert.Equal(HttpStatusCode.Created, planCreated.StatusCode);
        var plan = (await planCreated.Content.ReadFromJsonAsync<ProviderPlanResponse>())!;
        Assert.Equal(provider.Id, plan.ProviderAccountId);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/plans/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/providers/{provider.Id}")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var account = await db.ProviderAccounts.SingleAsync(x => x.Id == provider.Id);
        Assert.StartsWith($"env/dev/orgs/org/providers/{provider.Id}/credentials/", account.CredentialPath);
        Assert.DoesNotContain(credential, account.CredentialPath);
    }

    [Fact]
    public async Task WorkspaceApiStopsAndRestartsTheSameRetainedWorkspace()
    {
        using var client = Client(token);
        var initial = await Create(client);
        var url = $"{Root}/{initial.Id}";
        using var started = await client.PostAsync($"{url}/start?organizationId=org", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await CommitSyntheticOperation("start", (operation, vm, workspace) =>
        {
            vm.Phase = "ready";
            workspace.ProviderVolumeId = "retained-fixture";
            workspace.VolumeStatus = "mounted";
        });

        using var stopped = await client.PostAsync($"{url}/stop?organizationId=org", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        await CommitSyntheticOperation("stop", (operation, vm, workspace) =>
        {
            vm.Phase = "stopped";
            vm.ComputeReserved = false;
            workspace.VolumeStatus = "retained";
        });

        using var resumed = await client.PostAsync($"{url}/start?organizationId=org", null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var stored = await db.DevelopmentWorkspaces.Include(x => x.Vms).SingleAsync();
        Assert.Equal(initial.Id, stored.Id);
        Assert.Equal("retained-fixture", stored.ProviderVolumeId);
        var primary = Assert.Single(stored.Vms);
        Assert.Equal(2, primary.Generation);
        Assert.Equal("running", primary.DesiredState);
    }

    private async Task CommitSyntheticOperation(string kind,
        Action<Common.Models.DevelopmentWorkspaceOperation, Common.Models.DevelopmentWorkspaceVm, Common.Models.DevelopmentWorkspace> update)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceService>();
        var claim = Assert.IsType<Common.Models.DevelopmentWorkspaceOperation>(
            await service.ClaimOperationAsync("fixture-worker", default));
        Assert.Equal(kind, claim.Kind);
        await service.UpdateClaimedOperationAsync(claim.Id, claim.LeaseToken!, (operation, vm, workspace) =>
        {
            update(operation, vm, workspace);
            operation.Status = "completed";
            operation.LeaseOwner = null;
            operation.LeaseToken = null;
            operation.LeaseExpiresAt = null;
        }, default);
    }
}
