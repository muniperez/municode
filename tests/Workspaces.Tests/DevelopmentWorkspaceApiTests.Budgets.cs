using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workspaces.Common.Auth;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Providers;
using Workspaces.Common.Services;

namespace Workspaces.Tests;

public sealed partial class DevelopmentWorkspaceApiTests
{
    private const string Budgets = "/api/budgets";

    [Fact]
    public async Task ExceededBudgetNeverBlocksCreateOrStartAndAlertsOncePerThreshold()
    {
        using var owner = Client(token);
        var budget = await CreateBudget(owner, new("org", "Team", 100m, null, [1, 50, 100]));
        Assert.True(budget.Projection.Estimate);
        Assert.False(budget.Projection.ProviderChargesAvailable);
        Assert.Equal(0m, budget.Projection.ProjectedMonthly);
        Assert.Null(budget.Projection.CrossedThreshold);

        var workspace = await Create(owner);
        var listed = await ListBudgets(owner);
        Assert.Equal(2m, listed.Single().Projection.ProjectedMonthly);
        Assert.Equal(1, listed.Single().Projection.CrossedThreshold);

        await UpdateBudget(owner, listed.Single(), 1m);
        await CreateNamed(owner, "Over budget");
        var started = await owner.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());

        var over = (await ListBudgets(owner)).Single();
        Assert.Equal(76m, over.Projection.ProjectedMonthly);
        Assert.Equal(76m * 100m, over.Projection.PercentOfBudget);
        Assert.Equal(100, over.Projection.CrossedThreshold);
        Assert.True(over.Projection.OverBudget);
        Assert.True(over.Projection.Complete);
        Assert.Equal(.1m + 4m / 720m, over.Projection.ProjectedHourly, 4);
        var contributor = over.Projection.Contributors.First();
        Assert.Equal((workspace.Id, 1, 72m, 2m, "current"), (contributor.WorkspaceId, contributor.ReservedVmCount,
            contributor.ComputeMonthly, contributor.StorageMonthly, contributor.PricingStatus));
        Assert.Equal("cx-test", Assert.Single(contributor.Skus));
        Assert.Contains(over.Projection.Assumptions, x => x.Contains("never blocks", StringComparison.Ordinal));
        await ListBudgets(owner);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.WorkspaceId == workspace.Id);
        Assert.True(vm.ComputeReserved);
        Assert.Equal("running", vm.DesiredState);
        var alerts = await db.AuditEvents.Where(x => x.Action == "budget.threshold_crossed").ToListAsync();
        Assert.Equal([1, 100], alerts.Select(x => JsonDocument.Parse(x.Details!).RootElement.GetProperty("threshold").GetInt32()).Order());
        Assert.All(alerts, x => Assert.Equal(BudgetService.AlertActor, x.Actor));
        Assert.Contains(workspace.Id, alerts.Single(x => x.Details!.Contains("\"threshold\":100")).Details);
    }

    [Fact]
    public async Task BudgetProjectionGroupsByProviderAndFlagsCurrencyStaleAndUnknownPricing()
    {
        using var owner = Client(token);
        var euro = await Create(owner);
        var stale = await CreateNamed(owner, "Stale");
        var unknown = await CreateNamed(owner, "Unknown");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            db.ProviderAccounts.Add(new ProviderAccount { Id = "second", OrganizationId = "org", Name = "Droplets",
                ProviderType = CloudProvider.DigitalOcean, CredentialPath = "sensitive/second", CredentialGeneration = "synthetic" });
            await db.SaveChangesAsync();
            await db.DevelopmentWorkspaces.Where(x => x.Id == euro.Id).ExecuteUpdateAsync(x => x.SetProperty(w => w.Currency, "EUR"));
            await db.DevelopmentWorkspaces.Where(x => x.Id == stale.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(w => w.PricingUpdatedAt, DateTimeOffset.UtcNow.AddDays(-60)));
            await db.DevelopmentWorkspaces.Where(x => x.Id == unknown.Id).ExecuteUpdateAsync(x => x
                .SetProperty(w => w.PricingUpdatedAt, (DateTimeOffset?)null).SetProperty(w => w.ProviderAccountId, "second"));
        }

        var all = await CreateBudget(owner, new("org", "All", 50m, "usd"));
        Assert.Equal("USD", all.Currency);
        Assert.Equal([50, 80, 100], all.Thresholds);
        Assert.False(all.Projection.Complete);
        Assert.Equal(4m, all.Projection.ProjectedMonthly);
        Assert.Equal(euro.Id, Assert.Single(all.Projection.Excluded).WorkspaceId);
        Assert.Equal(3, all.Projection.Warnings.Count);
        Assert.Equal(["digital-ocean", "hetzner"], all.Projection.Providers.Select(x => x.ProviderType).Order());
        Assert.Equal("stale", all.Projection.Contributors.Single(x => x.WorkspaceId == stale.Id).PricingStatus);
        Assert.Equal("unknown", all.Projection.Contributors.Single(x => x.WorkspaceId == unknown.Id).PricingStatus);

        var scoped = await CreateBudget(owner, new("org", "Droplets only", 50m, ProviderAccountIds: ["second"]));
        var provider = Assert.Single(scoped.Projection.Providers);
        Assert.Equal(("second", "Droplets", "digital-ocean", 2m), (provider.ProviderAccountId, provider.ProviderName,
            provider.ProviderType, provider.ProjectedMonthly));
        Assert.Empty(scoped.Projection.Excluded);
    }

    [Fact]
    public async Task BudgetsValidateAuditAndUseRevisions()
    {
        using var owner = Client(token);
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Bad", 0m)), 400, "budget_amount_invalid");
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Bad", 1.234m)), 400, "budget_amount_invalid");
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Bad", 1m, null, [10, 20, 30, 40])),
            400, "budget_thresholds_invalid");
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Bad", 1m, "US1")), 400, "budget_currency_invalid");
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Bad", 1m, ProviderAccountIds: ["foreign"])),
            400, "budget_scope_invalid");
        var budget = await CreateBudget(owner, new("org", "Team", 10m));
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", " team ", 1m)), 409, "budget_name_taken");
        var updated = await UpdateBudget(owner, budget, 20m);
        Assert.Equal(budget.Revision + 1, updated.Revision);
        await AssertBudgetError(await owner.PutAsJsonAsync($"{Budgets}/{budget.Id}",
            new UpdateBudgetRequest(budget.Revision, "Team", 30m)), 409, "revision_conflict");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{Budgets}/{budget.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"{Budgets}/{budget.Id}")).StatusCode);
        Assert.Empty(await ListBudgets(owner));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        Assert.Equal(["budget.created", "budget.deleted", "budget.updated"],
            await db.AuditEvents.Where(x => x.Action.StartsWith("budget.")).Select(x => x.Action).OrderBy(x => x).ToListAsync());
    }

    [Fact]
    public async Task BudgetApiKeysNeedBudgetScopesAndStayInTheirOrganization()
    {
        string reader, unrelated;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
            reader = AddKey(db, tokens, "org", [ApiKeyScopes.BudgetsRead]);
            unrelated = AddKey(db, tokens, "org", [ApiKeyScopes.WorkspacesRead, ApiKeyScopes.WorkspacesWrite]);
            await db.SaveChangesAsync();
        }
        using var owner = Client(token);
        var budget = await CreateBudget(owner, new("org", "Team", 10m));
        using var read = Client(reader);
        Assert.Single(await ListBudgets(read));
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Other", 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsJsonAsync($"{Budgets}/{budget.Id}",
            new UpdateBudgetRequest(budget.Revision, "Team", 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"{Budgets}/{budget.Id}")).StatusCode);
        using var none = Client(unrelated);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"{Budgets}?organizationId=org")).StatusCode);
        using var other = Client(otherToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Budgets}?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{Budgets}/{budget.Id}")).StatusCode);
        Assert.Single(await ListBudgets(owner));
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, true)]
    [InlineData(OrganizationRole.Admin, true)]
    [InlineData(OrganizationRole.Developer, false)]
    [InlineData(OrganizationRole.Viewer, false)]
    public async Task EveryMemberReadsBudgetsButOnlyOwnersAndAdminsManage(OrganizationRole role, bool manage)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.Users.Add(new User { Id = "member", Email = "member@example.test" });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "membership", OrganizationId = "org", UserId = "member", Role = role });
        await db.SaveChangesAsync();
        var authorization = scope.ServiceProvider.GetRequiredService<ControlPlaneAuthorization>();
        var actor = new ActorIdentity("browser_session", "member", null, null, new HashSet<string>());
        Assert.True(await authorization.CanAccessOrganizationAsync(actor, "org", OrganizationAction.ReadBudgets, default));
        Assert.Equal(manage, await authorization.CanAccessOrganizationAsync(actor, "org", OrganizationAction.ManageBudgets, default));
        Assert.False(await authorization.CanAccessOrganizationAsync(actor, "other", OrganizationAction.ReadBudgets, default));
    }

    private static async Task<DevelopmentWorkspaceResponse> CreateNamed(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Root, Request() with { Name = name, IdempotencyKey = name });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!;
    }

    private static async Task<BudgetResponse> CreateBudget(HttpClient client, CreateBudgetRequest request)
    {
        var response = await client.PostAsJsonAsync(Budgets, request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BudgetResponse>())!;
    }

    private static async Task<BudgetResponse> UpdateBudget(HttpClient client, BudgetResponse budget, decimal amount)
    {
        var response = await client.PutAsJsonAsync($"{Budgets}/{budget.Id}",
            new UpdateBudgetRequest(budget.Revision, budget.Name, amount, budget.Thresholds, budget.ProviderAccountIds));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BudgetResponse>())!;
    }

    private static async Task<List<BudgetResponse>> ListBudgets(HttpClient client)
    {
        var response = await client.GetAsync($"{Budgets}?organizationId=org");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<List<BudgetResponse>>())!;
    }

    private static async Task AssertBudgetError(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }
}
