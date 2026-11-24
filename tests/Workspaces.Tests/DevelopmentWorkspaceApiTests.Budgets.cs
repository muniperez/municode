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
    public async Task ExceededBudgetNeverBlocksCreateOrStartAndBackgroundEvaluationAlertsOncePerThreshold()
    {
        using var owner = Client(token);
        var budget = await CreateBudget(owner, new("org", "Team", 100m, null, [1, 50, 100]));
        Assert.True(budget.Projection.Estimate);
        Assert.False(budget.Projection.ProviderChargesAvailable);
        Assert.Equal(0m, budget.Projection.ProjectedMonthly);
        Assert.Null(budget.Projection.CrossedThreshold);
        Assert.Equal(new BudgetNotificationSettingsResponse(true, false, null, false), budget.Notifications);

        var workspace = await Create(owner);
        var listed = await ListBudgets(owner);
        await ListBudgets(owner);
        Assert.Equal(2m, listed.Single().Projection.ProjectedMonthly);
        Assert.Equal(1, listed.Single().Projection.CrossedThreshold);
        // Reading budgets is side-effect free; only the background evaluation records alerts.
        Assert.Null(listed.Single().LastAlert);
        Assert.Empty(await BudgetAlertsAsync());
        Assert.Equal(1, await EvaluateBudgetsConcurrentlyAsync());
        Assert.Equal(0, await EvaluateBudgetsConcurrentlyAsync());
        var alerted = (await ListBudgets(owner)).Single();
        Assert.Equal(1, alerted.LastAlert!.Threshold);
        var delivery = Assert.Single(alerted.Deliveries);
        Assert.Equal(("in_app", 1, "delivered"), (delivery.Channel, delivery.Threshold, delivery.Status));

        // Changing only notification channels keeps the alert baseline, so nothing is re-sent.
        var renamed = await PutBudget(owner, alerted.Id, new(alerted.Revision, "Team renamed", 100m, alerted.Thresholds,
            alerted.ProviderAccountIds, new(InApp: true)));
        Assert.Equal(1, renamed.LastAlert!.Threshold);
        Assert.Equal(0, await EvaluateBudgetsConcurrentlyAsync());

        await UpdateBudget(owner, renamed, 1m);
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
        Assert.Null(over.LastAlert);
        var contributor = over.Projection.Contributors.First();
        Assert.Equal((workspace.Id, 1, 72m, 2m, "current"), (contributor.WorkspaceId, contributor.ReservedVmCount,
            contributor.ComputeMonthly, contributor.StorageMonthly, contributor.PricingStatus));
        Assert.Equal("cx-test", Assert.Single(contributor.Skus));
        Assert.Contains(over.Projection.Assumptions, x => x.Contains("never blocks", StringComparison.Ordinal));
        Assert.Single(await BudgetAlertsAsync());
        Assert.Equal(1, await EvaluateBudgetsConcurrentlyAsync());
        Assert.Equal(0, await EvaluateBudgetsConcurrentlyAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.WorkspaceId == workspace.Id);
        Assert.True(vm.ComputeReserved);
        Assert.Equal("running", vm.DesiredState);
        var alerts = await BudgetAlertsAsync();
        Assert.Equal([1, 100], alerts.Select(x => JsonDocument.Parse(x.Details!).RootElement.GetProperty("threshold").GetInt32()).Order());
        Assert.All(alerts, x => Assert.Equal(BudgetService.AlertActor, x.Actor));
        var details = JsonDocument.Parse(alerts.Single(x => x.Details!.Contains("\"threshold\":100")).Details!).RootElement;
        Assert.Equal([1, 50, 100], details.GetProperty("crossedThresholds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(workspace.Id, details.GetProperty("contributors")[0].GetProperty("workspaceId").GetString());
        Assert.NotEmpty(details.GetProperty("assumptions").EnumerateArray());
        Assert.Equal(2, await db.BudgetNotifications.CountAsync(x => x.Channel == "in_app" && x.Status == "delivered"));
        Assert.False(await db.BudgetNotifications.AnyAsync(x => x.Channel == "webhook"));
    }

    [Fact]
    public async Task BudgetWebhookSettingsValidateTargetsAndRevealSigningSecretsOnlyOnce()
    {
        using var owner = Client(token);
        await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Hooks", 10m,
            Notifications: new(Webhook: true))), 400, "budget_webhook_url_required");
        foreach (var url in new[] { "http://hooks.example.com/budget", "https://127.0.0.1/budget", "https://10.1.2.3/budget",
                     "https://[::1]/budget", "https://169.254.169.254/latest", "https://user:pass@hooks.example.com/budget",
                     "https://localhost/budget", "https://hooks.example.com/budget#fragment", "not a url" })
            await AssertBudgetError(await owner.PostAsJsonAsync(Budgets, new CreateBudgetRequest("org", "Hooks", 10m,
                Notifications: new(Webhook: true, WebhookUrl: url))), 400, "budget_webhook_url_invalid");

        var created = await CreateBudget(owner, new("org", "Hooks", 10m, Notifications:
            new(InApp: false, Webhook: true, WebhookUrl: "https://hooks.example.com/budget?token=sensitive-token")));
        var secret = created.WebhookSigningSecret!;
        Assert.StartsWith("mcwh_", secret);
        Assert.Equal(new BudgetNotificationSettingsResponse(false, true, "https://hooks.example.com", true), created.Notifications);
        Assert.Equal(secret, Assert.Single(factory.Secrets.Values).Value);
        var listed = await owner.GetStringAsync($"{Budgets}?organizationId=org");
        Assert.DoesNotContain("sensitive-token", listed);
        Assert.DoesNotContain(secret, listed);
        Assert.DoesNotContain("webhookSigningSecret\":\"", listed);

        var kept = await PutBudget(owner, created.Id, new(created.Revision, "Hooks", 10m));
        Assert.Null(kept.WebhookSigningSecret);
        Assert.Equal(created.Notifications, kept.Notifications);
        var rotated = await PutBudget(owner, created.Id, new(kept.Revision, "Hooks", 10m, Notifications: new(RotateWebhookSecret: true)));
        Assert.StartsWith("mcwh_", rotated.WebhookSigningSecret);
        Assert.NotEqual(secret, rotated.WebhookSigningSecret);
        Assert.Equal(rotated.WebhookSigningSecret, Assert.Single(factory.Secrets.Values).Value);
        var paused = await PutBudget(owner, created.Id, new(rotated.Revision, "Hooks", 10m, Notifications: new(Webhook: false)));
        Assert.Equal(new BudgetNotificationSettingsResponse(false, false, "https://hooks.example.com", true), paused.Notifications);
        var removed = await PutBudget(owner, created.Id, new(paused.Revision, "Hooks", 10m, Notifications: new(WebhookUrl: "")));
        Assert.Equal(new BudgetNotificationSettingsResponse(false, false, null, false), removed.Notifications);
        Assert.Empty(factory.Secrets.Values);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var audit = string.Join('\n', await db.AuditEvents.Where(x => x.Action.StartsWith("budget.")).Select(x => x.Details).ToListAsync());
        Assert.DoesNotContain("sensitive-token", audit);
        Assert.DoesNotContain(secret, audit);
    }

    private async Task<List<AuditEvent>> BudgetAlertsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>().AuditEvents.AsNoTracking()
            .Where(x => x.Action == BudgetService.ThresholdCrossedEvent).ToListAsync();
    }

    // Separate scopes use separate connections, so evaluators genuinely race for the same thresholds.
    private async Task<int> EvaluateBudgetsConcurrentlyAsync()
    {
        var scopes = Enumerable.Range(0, 4).Select(_ => factory.Services.CreateAsyncScope()).ToList();
        try
        {
            var claimed = await Task.WhenAll(scopes.Select(scope => Task.Run(() =>
                scope.ServiceProvider.GetRequiredService<BudgetService>().EvaluateAlertsAsync("org", default))));
            return claimed.Sum();
        }
        finally
        {
            foreach (var scope in scopes) await scope.DisposeAsync();
        }
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
                .SetProperty(w => w.PricingUpdatedAt, (DateTimeOffset?)null).SetProperty(w => w.StorageMonthlyRate, 0m)
                .SetProperty(w => w.ProviderAccountId, "second"));
        }

        var all = await CreateBudget(owner, new("org", "All", 50m, "usd"));
        Assert.Equal("USD", all.Currency);
        Assert.Equal([50, 80, 100], all.Thresholds);
        Assert.False(all.Projection.Complete);
        Assert.Equal(2m, all.Projection.ProjectedMonthly);
        Assert.Equal(euro.Id, Assert.Single(all.Projection.Excluded).WorkspaceId);
        Assert.Equal(3, all.Projection.Warnings.Count);
        Assert.Equal(["digital-ocean", "hetzner"], all.Projection.Providers.Select(x => x.ProviderType).Order());
        Assert.Equal("stale", all.Projection.Contributors.Single(x => x.WorkspaceId == stale.Id).PricingStatus);
        Assert.Equal("unknown", all.Projection.Contributors.Single(x => x.WorkspaceId == unknown.Id).PricingStatus);

        var scoped = await CreateBudget(owner, new("org", "Droplets only", 50m, ProviderAccountIds: ["second"]));
        var provider = Assert.Single(scoped.Projection.Providers);
        Assert.Equal(("second", "Droplets", "digital-ocean", 0m, false), (provider.ProviderAccountId, provider.ProviderName,
            provider.ProviderType, provider.ProjectedMonthly, provider.Complete));
        Assert.Contains(provider.Assumptions, x => x.StartsWith("Missing price: 1 resource(s)", StringComparison.Ordinal));
        Assert.Null(scoped.Projection.Contributors.Single().Lines.Single().Amount);
        Assert.Empty(scoped.Projection.Excluded);
    }

    [Fact]
    public async Task BudgetAcrossTwoProviderBillingModelsShowsAssumptionsAndAlertsWithoutBlockingCreateOrStart()
    {
        var effective = DateTimeOffset.UtcNow.AddDays(-7);
        factory.Policy.PriceCatalog = new()
        {
            ["hetzner-cx-test"] = new() { Provider = "hetzner", Region = "nbg1", Sku = "cx-test", BillingModel = BudgetPricing.HourlyMonthlyCap,
                HourlyRate = .1m, MonthlyRate = 50m, Currency = "USD", Source = "Synthetic Hetzner list", EffectiveFrom = effective },
            ["hetzner-volumes"] = new() { Provider = "hetzner", Resource = BudgetPricing.Storage, BillingModel = BudgetPricing.Monthly,
                MonthlyRate = .05m, Currency = "USD", Source = "Synthetic Hetzner list", EffectiveFrom = effective },
            ["droplet-s-test"] = new() { Provider = "digital-ocean", Sku = "s-test", BillingModel = BudgetPricing.Hourly,
                HourlyRate = .2m, Currency = "USD", Source = "Synthetic DigitalOcean list", EffectiveFrom = effective }
        };
        using var owner = Client(token);
        var hetzner = await Create(owner);
        var droplet = await CreateNamed(owner, "Droplet");
        foreach (var id in new[] { hetzner.Id, droplet.Id })
        {
            var started = await owner.PostAsync($"{Root}/{id}/start?organizationId=org", null);
            Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            db.ProviderAccounts.Add(new ProviderAccount { Id = "second", OrganizationId = "org", Name = "Droplets",
                ProviderType = CloudProvider.DigitalOcean, CredentialPath = "sensitive/second", CredentialGeneration = "synthetic" });
            await db.SaveChangesAsync();
            await db.DevelopmentWorkspaces.Where(x => x.Id == droplet.Id).ExecuteUpdateAsync(x => x.SetProperty(w => w.ProviderAccountId, "second"));
            await db.DevelopmentWorkspaceVms.Where(x => x.WorkspaceId == droplet.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.ProviderAccountId, "second").SetProperty(v => v.Sku, "s-test"));
        }

        var budget = await CreateBudget(owner, new("org", "Team", 100m, null, [80, 100]));
        var projection = budget.Projection;
        Assert.Equal((197m, 197m, 100, true, true), (projection.ProjectedMonthly, projection.PercentOfBudget,
            projection.CrossedThreshold, projection.OverBudget, projection.Complete));
        var capped = projection.Providers.Single(x => x.ProviderType == "hetzner");
        Assert.Equal((50m, 1m), (capped.ComputeMonthly, capped.StorageMonthly));
        Assert.Contains(capped.Assumptions, x => x.Contains("capped at 50.00 USD/VM per month", StringComparison.Ordinal));
        Assert.Contains(capped.Assumptions, x => x.StartsWith("Storage in all regions: billed monthly at 0.05 USD/GB", StringComparison.Ordinal));
        Assert.DoesNotContain(capped.Assumptions, x => x.StartsWith("Fallback rate", StringComparison.Ordinal));
        var hourly = projection.Providers.Single(x => x.ProviderType == "digital-ocean");
        Assert.Equal((144m, 2m), (hourly.ComputeMonthly, hourly.StorageMonthly));
        Assert.Contains(hourly.Assumptions, x => x.StartsWith("Compute s-test in all regions: billed per started hour at 0.20 USD/VM-hour.", StringComparison.Ordinal));
        Assert.Contains(hourly.Assumptions, x => x.StartsWith("Fallback rate: 1 volume(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(hourly.Assumptions, x => x.Contains("capped", StringComparison.Ordinal));
        Assert.Empty(await BudgetAlertsAsync());
        Assert.Equal(1, await EvaluateBudgetsConcurrentlyAsync());
        Assert.Equal(100, (await ListBudgets(owner)).Single().LastAlert!.Threshold);

        var later = await CreateNamed(owner, "After alert");
        var restarted = await owner.PostAsync($"{Root}/{later.Id}/start?organizationId=org", null);
        Assert.True(restarted.IsSuccessStatusCode, await restarted.Content.ReadAsStringAsync());
        await using var verify = factory.Services.CreateAsyncScope();
        var store = verify.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var vms = await store.DevelopmentWorkspaceVms.ToListAsync();
        Assert.Equal(3, vms.Count);
        Assert.All(vms, vm => Assert.True(vm.ComputeReserved && vm.DesiredState == "running"));
        var alert = Assert.Single(await store.AuditEvents.Where(x => x.Action == BudgetService.ThresholdCrossedEvent).ToListAsync());
        var details = JsonDocument.Parse(alert.Details!).RootElement;
        Assert.Equal((100, 197m), (details.GetProperty("threshold").GetInt32(), details.GetProperty("projectedMonthly").GetDecimal()));
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

    private static async Task<BudgetResponse> PutBudget(HttpClient client, string id, UpdateBudgetRequest request)
    {
        var response = await client.PutAsJsonAsync($"{Budgets}/{id}", request);
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
