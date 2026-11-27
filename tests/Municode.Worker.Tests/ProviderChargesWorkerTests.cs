using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Municode.Common.Models;
using Municode.Common.Providers;
using Municode.Common.Providers.DigitalOcean;
using Municode.Common.Providers.Vultr;
using Municode.Common.Services;
using Municode.Worker.Budgets;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

// Provider-reported charges retrieved by the worker from synthetic billing endpoints (#109).
public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Fact]
    public async Task ProviderChargesAreRecordedPerAccountAndPeriodKeepingTheLastAmountWhenARefreshFails()
    {
        var start = clock.GetUtcNow();
        clock.Advance(new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddDays(1) - start);
        db.ProviderAccounts.AddRange(
            new ProviderAccount { Id = "do", OrganizationId = "org", Name = "Droplets", ProviderType = CloudProvider.DigitalOcean,
                CredentialPath = "synthetic/do", CredentialGeneration = "fixture" },
            new ProviderAccount { Id = "vultr", OrganizationId = "org", Name = "Vultr", ProviderType = CloudProvider.Vultr,
                CredentialPath = "synthetic/vultr", CredentialGeneration = "fixture" });
        await db.SaveChangesAsync();
        var billing = new SyntheticBilling();
        var collector = new ProviderChargesCollector(db, new BillingFactory(billing), new IdGenerator(), clock,
            Options.Create(new ProviderChargesOptions()), NullLogger<ProviderChargesCollector>.Instance);

        // Organizations without budgets are never asked for billing data.
        Assert.Equal(0, await collector.RefreshDueAsync(default));
        Assert.Empty(billing.Requests);
        var (budgets, _, _) = BudgetWorker();
        await budgets.CreateAsync(new("org", "Team", 100m), "actor", default);

        billing.DigitalOcean = (HttpStatusCode.OK,
            $$"""{"month_to_date_usage":"11.21","generated_at":"{{clock.GetUtcNow():O}}"}""");
        billing.Vultr = (HttpStatusCode.Forbidden, """{"error":"Unauthorized IP address: sensitive-detail"}""");
        Assert.Equal(2, await collector.RefreshDueAsync(default));
        // Hetzner has no billing API, so its credential is never read for charges.
        Assert.Equal(["GET customers/my/balance", "GET account"], billing.Requests);
        var period = ProviderChargeService.Period(clock.GetUtcNow());
        var digitalOcean = await Charge("do");
        Assert.Equal((ProviderCharge.Ok, 11.21m, "USD", period, DigitalOceanClient.ChargesSource),
            (digitalOcean.Status, digitalOcean.Amount, digitalOcean.Currency, digitalOcean.Period, digitalOcean.Source));
        var vultr = await Charge("vultr");
        Assert.Equal((ProviderCharge.Failed, null), (vultr.Status, vultr.Amount));
        Assert.Equal("HTTP 403: the provider credential cannot read billing data", vultr.LastError);

        // Refreshes are spaced by the configured interval, including after failures.
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await collector.RefreshDueAsync(default));
        Assert.Equal(2, billing.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(2));
        billing.DigitalOcean = (HttpStatusCode.InternalServerError, "{}");
        billing.Vultr = (HttpStatusCode.OK, """{"account":{"pending_charges":57.03}}""");
        Assert.Equal(2, await collector.RefreshDueAsync(default));
        digitalOcean = await Charge("do");
        Assert.Equal((ProviderCharge.Failed, 11.21m, "HTTP 500"), (digitalOcean.Status, digitalOcean.Amount, digitalOcean.LastError));
        Assert.True(digitalOcean.RetrievedAt < digitalOcean.AttemptedAt);
        vultr = await Charge("vultr");
        Assert.Equal((ProviderCharge.Ok, 57.03m, "USD", null), (vultr.Status, vultr.Amount, vultr.Currency, vultr.LastError));
        Assert.Equal(clock.GetUtcNow(), vultr.AsOf);
        Assert.Equal(2, await db.ProviderCharges.CountAsync());
    }

    private async Task<ProviderCharge> Charge(string account)
    {
        db.ChangeTracker.Clear();
        return await db.ProviderCharges.AsNoTracking().SingleAsync(x => x.ProviderAccountId == account);
    }

    private sealed class SyntheticBilling : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public (HttpStatusCode Status, string Body) DigitalOcean { get; set; }
        public (HttpStatusCode Status, string Body) Vultr { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path[(path.IndexOf("/v2/", StringComparison.Ordinal) + 4)..]}");
            var (status, body) = request.RequestUri.Host == "do.billing.test" ? DigitalOcean : Vultr;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class BillingFactory(SyntheticBilling billing) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!)
    {
        public override Task<IDevelopmentWorkspaceProvider> CreateAsync(string organizationId, string providerAccountId,
            CancellationToken ct, bool allowDisabled = true) => Task.FromResult<IDevelopmentWorkspaceProvider>(providerAccountId switch
            {
                "do" => new DigitalOceanClient(new HttpClient(billing) { BaseAddress = new Uri("https://do.billing.test/v2/") },
                    new DigitalOceanOptions { ApiToken = "synthetic" }),
                "vultr" => new VultrClient(new HttpClient(billing) { BaseAddress = new Uri("https://vultr.billing.test/v2/") },
                    new VultrOptions { ApiKey = "synthetic" }),
                _ => throw new InvalidOperationException("Only accounts with a billing API are asked for charges.")
            });
    }
}
