using Workspaces.Common.Models;
using Workspaces.Common.Providers;
using Workspaces.Common.Services;

namespace Workspaces.Tests;

public sealed class BudgetPricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HourlyBillingWithMonthlyCapBillsStartedHoursUntilTheCap()
    {
        var entry = Entry(BudgetPricing.HourlyMonthlyCap, hourly: .0080m, monthly: 4.99m);
        Assert.Equal(.0080m, BudgetPricing.Charge(entry, 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(.0160m, BudgetPricing.Charge(entry, 1, TimeSpan.FromMinutes(61)));
        Assert.Equal(4.984m, BudgetPricing.Charge(entry, 1, TimeSpan.FromHours(623)));
        Assert.Equal(4.99m, BudgetPricing.Charge(entry, 1, TimeSpan.FromHours(624)));
        Assert.Equal(4.99m, BudgetPricing.Charge(entry, 1, TimeSpan.FromHours(720)));
        Assert.Equal(9.98m, BudgetPricing.Charge(entry, 2, TimeSpan.FromHours(720)));
        Assert.Equal(0m, BudgetPricing.Charge(entry, 1, TimeSpan.Zero));
    }

    [Fact]
    public void PerSecondAndPlainHourlyBillingDifferOnlyOnPartialHours()
    {
        var perSecond = Entry(BudgetPricing.PerSecond, hourly: .36m);
        var hourly = Entry(BudgetPricing.Hourly, hourly: .36m);
        Assert.Equal(.009m, BudgetPricing.Charge(perSecond, 1, TimeSpan.FromSeconds(90)));
        Assert.Equal(.36m, BudgetPricing.Charge(hourly, 1, TimeSpan.FromSeconds(90)));
        Assert.Equal(.0001m, BudgetPricing.Charge(perSecond, 1, TimeSpan.FromSeconds(.2)));
        Assert.Equal(259.2m, BudgetPricing.Charge(perSecond, 1, TimeSpan.FromHours(720)));
        Assert.Equal(259.2m, BudgetPricing.Charge(hourly, 1, TimeSpan.FromHours(720)));
    }

    [Fact]
    public void MinimumChargeAndMinimumBilledDurationApplyPerResourceOnlyWhenBilled()
    {
        var charge = Entry(BudgetPricing.PerSecond, hourly: .36m, minimumCharge: .05m);
        Assert.Equal(.05m, BudgetPricing.Charge(charge, 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(.36m, BudgetPricing.Charge(charge, 1, TimeSpan.FromHours(1)));
        Assert.Equal(0m, BudgetPricing.Charge(charge, 1, TimeSpan.Zero));
        var duration = Entry(BudgetPricing.PerSecond, hourly: .36m, minimumSeconds: 60);
        Assert.Equal(.006m, BudgetPricing.Charge(duration, 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(.009m, BudgetPricing.Charge(duration, 1, TimeSpan.FromSeconds(90)));
        var storage = Entry(BudgetPricing.Monthly, monthly: .044m, minimumCharge: 1m, resource: BudgetPricing.Storage);
        Assert.Equal(1m, BudgetPricing.Charge(storage, 10, TimeSpan.FromHours(720)));
        Assert.Equal(2.2m, BudgetPricing.Charge(storage, 50, TimeSpan.FromHours(720)));
        storage.MinimumCharge = null;
        Assert.Equal(1.1m, BudgetPricing.Charge(storage, 50, TimeSpan.FromHours(360)));
    }

    [Fact]
    public void CatalogPrefersAccountThenRegionThenLatestEffectiveEntryAndIgnoresFutureOrInvalidOnes()
    {
        var catalog = new Dictionary<string, BudgetPriceCatalogEntry>
        {
            ["any-region"] = Entry(BudgetPricing.Hourly, hourly: 1m),
            ["region-old"] = Entry(BudgetPricing.Hourly, hourly: 2m, region: "nbg1", effective: Now.AddDays(-20)),
            ["region-new"] = Entry(BudgetPricing.Hourly, hourly: 3m, region: "nbg1", effective: Now.AddDays(-10)),
            ["region-future"] = Entry(BudgetPricing.Hourly, hourly: 4m, region: "nbg1", effective: Now.AddDays(1)),
            ["account"] = Entry(BudgetPricing.Hourly, hourly: 5m, account: "special"),
            ["invalid"] = Entry(BudgetPricing.HourlyMonthlyCap, hourly: 6m, region: "nbg1", account: "account")
        };
        Assert.Equal("region-new", Find(catalog, "account", "nbg1")?.Id);
        Assert.Equal("any-region", Find(catalog, "account", "fsn1")?.Id);
        Assert.Equal("account", Find(catalog, "special", "nbg1")?.Id);
        Assert.Null(BudgetPricing.Find(catalog, CloudProvider.Vultr, "account", BudgetPricing.Compute, "nbg1", "cx22", Now));
        Assert.Null(BudgetPricing.Find(catalog, CloudProvider.Hetzner, "account", BudgetPricing.Compute, "nbg1", "cx32", Now));
        Assert.Equal("monthly rate must be positive", BudgetPricing.Invalid(catalog["invalid"]));
        Assert.Equal("currency must be a three-letter ISO 4217 code", BudgetPricing.Invalid(Entry(BudgetPricing.Hourly, hourly: 1m, currency: "eur")));
        var undated = Entry(BudgetPricing.Hourly, hourly: 1m);
        undated.EffectiveFrom = null;
        Assert.Equal("effective date is required", BudgetPricing.Invalid(undated));
    }

    [Fact]
    public void ProjectionUsesCatalogFallsBackToRecordedRatesAndFlagsMissingAndStalePrices()
    {
        var policy = Policy(new()
        {
            ["cx22"] = Entry(BudgetPricing.HourlyMonthlyCap, hourly: .01m, monthly: 5m, currency: "USD"),
            ["old-cx32"] = Entry(BudgetPricing.Hourly, hourly: .02m, sku: "cx32", currency: "USD", effective: Now.AddDays(-200)),
            ["broken"] = Entry(BudgetPricing.Hourly, hourly: 0m, sku: "cx42", currency: "USD")
        });
        var capped = Workspace("capped", 0, Vm("capped-vm", "cx22", .5m));
        var stale = Workspace("stale", 0, Vm("stale-vm", "cx32", .5m));
        var fallback = Workspace("fallback", 20, Vm("fallback-vm", "cx42", .1m));
        fallback.StorageMonthlyRate = 2m;
        var missing = Workspace("missing", 10, Vm("missing-vm", "cx52", 0m));

        var projection = BudgetProjector.Project(Budget("USD", 100m), [50, 100], [], [capped, stale, fallback, missing],
            Accounts(), policy, Now);

        var lines = projection.Contributors.SelectMany(x => x.Lines).ToDictionary(x => x.ResourceId);
        Assert.Equal(("catalog", "cx22", 5m, "current"), (lines["capped-vm"].Source, lines["capped-vm"].CatalogEntryId,
            lines["capped-vm"].Amount, lines["capped-vm"].Status));
        Assert.Equal(("catalog", 14.4m, "stale"), (lines["stale-vm"].Source, lines["stale-vm"].Amount, lines["stale-vm"].Status));
        Assert.Equal(("fallback", 72m, "current"), (lines["fallback-vm"].Source, lines["fallback-vm"].Amount, lines["fallback-vm"].Status));
        Assert.Equal(("fallback", 2m), (lines["fallback"].Source, lines["fallback"].Amount));
        Assert.Equal(("missing", (decimal?)null), (lines["missing-vm"].Status, lines["missing-vm"].Amount));
        Assert.Equal(("missing", (decimal?)null), (lines["missing"].Status, lines["missing"].Amount));
        Assert.Equal(5m + 14.4m + 74m, projection.ProjectedMonthly);
        Assert.False(projection.Complete);
        Assert.Equal("unknown", projection.Contributors.Single(x => x.WorkspaceId == "missing").PricingStatus);
        Assert.Equal("stale", projection.Contributors.Single(x => x.WorkspaceId == "stale").PricingStatus);
        Assert.Contains(projection.Warnings, x => x.Contains("missing pricing", StringComparison.Ordinal));
        Assert.Contains(projection.Warnings, x => x.Contains("1 workspace(s) use pricing of unknown age or older", StringComparison.Ordinal));
        Assert.Contains(projection.Warnings, x => x.Contains("broken (hourly rate must be positive)", StringComparison.Ordinal));
        var provider = Assert.Single(projection.Providers);
        Assert.False(provider.Complete);
        Assert.Contains(provider.Assumptions, x => x.StartsWith("Compute cx22 in all regions: billed per started hour at 0.01 USD/VM-hour, capped at 5.00 USD/VM per month.", StringComparison.Ordinal));
        Assert.Contains(provider.Assumptions, x => x.Contains("Catalog entry old-cx32", StringComparison.Ordinal) && x.EndsWith("may be outdated.", StringComparison.Ordinal));
        Assert.Contains(provider.Assumptions, x => x.StartsWith("Fallback rate: 1 VM(s)", StringComparison.Ordinal));
        Assert.Contains(provider.Assumptions, x => x.StartsWith("Fallback rate: 1 volume(s)", StringComparison.Ordinal));
        Assert.Contains(provider.Assumptions, x => x.StartsWith("Missing price: 2 resource(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectionNeverSumsMixedCurrencies()
    {
        var policy = Policy(new()
        {
            ["compute-eur"] = Entry(BudgetPricing.Hourly, hourly: .01m, currency: "EUR"),
            ["storage-usd"] = Entry(BudgetPricing.Monthly, monthly: .1m, currency: "USD", resource: BudgetPricing.Storage)
        });
        var mixed = Workspace("mixed", 10, Vm("mixed-vm", "cx22", .1m));
        var euro = Workspace("euro", 0, Vm("euro-vm", "cx22", .1m));
        var dollars = Workspace("dollars", 10);

        var usd = BudgetProjector.Project(Budget("USD", 10m), [100], [], [mixed, euro, dollars], Accounts(), policy, Now);
        Assert.Equal(1m, usd.ProjectedMonthly);
        Assert.Equal(["dollars"], usd.Contributors.Select(x => x.WorkspaceId));
        Assert.Equal(("EUR, USD", "Priced in EUR and USD; this budget is in USD. It is not converted or summed."),
            (usd.Excluded.Single(x => x.WorkspaceId == "mixed").Currency, usd.Excluded.Single(x => x.WorkspaceId == "mixed").Reason));
        Assert.Equal("EUR", usd.Excluded.Single(x => x.WorkspaceId == "euro").Currency);

        var eur = BudgetProjector.Project(Budget("EUR", 10m), [100], [], [mixed, euro, dollars], Accounts(), policy, Now);
        Assert.Equal(7.2m, eur.ProjectedMonthly);
        Assert.Equal(["euro"], eur.Contributors.Select(x => x.WorkspaceId));
        Assert.Equal(["dollars", "mixed"], eur.Excluded.Select(x => x.WorkspaceId).Order());
    }

    private static BudgetCatalogMatch? Find(Dictionary<string, BudgetPriceCatalogEntry> catalog, string account, string region) =>
        BudgetPricing.Find(catalog, CloudProvider.Hetzner, account, BudgetPricing.Compute, region, "cx22", Now);

    private static BudgetPriceCatalogEntry Entry(string model, decimal? hourly = null, decimal? monthly = null,
        decimal? minimumCharge = null, int? minimumSeconds = null, string resource = BudgetPricing.Compute, string? region = null,
        string? account = null, string sku = "cx22", string currency = "EUR", DateTimeOffset? effective = null) => new()
    {
        Provider = "hetzner", ProviderAccountId = account, Resource = resource, Region = region,
        Sku = resource == BudgetPricing.Compute ? sku : null, BillingModel = model, HourlyRate = hourly, MonthlyRate = monthly,
        MinimumCharge = minimumCharge, MinimumBillableSeconds = minimumSeconds, Currency = currency,
        Source = "Synthetic price list", EffectiveFrom = effective ?? Now.AddDays(-30)
    };

    private static DevelopmentWorkspaceOptions Policy(Dictionary<string, BudgetPriceCatalogEntry> catalog) =>
        new() { PriceCatalog = catalog, MaxPricingAgeDays = 30, PriceCatalogMaxAgeDays = 90 };

    private static Budget Budget(string currency, decimal amount) => new()
        { Id = "budget", OrganizationId = "org", Name = "Budget", Currency = currency, MonthlyAmount = amount, CreatedBy = "test" };

    private static Dictionary<string, ProviderAccount> Accounts() => new()
    {
        ["account"] = new() { Id = "account", OrganizationId = "org", Name = "Hetzner", ProviderType = CloudProvider.Hetzner,
            CredentialPath = "synthetic", CredentialGeneration = "synthetic" }
    };

    private static DevelopmentWorkspaceVm Vm(string id, string sku, decimal rate) => new()
        { Id = id, Kind = DevelopmentWorkspaceVmKinds.Primary, Region = "nbg1", Sku = sku, ComputeHourlyRate = rate, ComputeReserved = true };

    private static DevelopmentWorkspace Workspace(string id, int volumeGb, params DevelopmentWorkspaceVm[] vms) => new()
    {
        Id = id, Name = id, OrganizationId = "org", ProviderAccountId = "account", Region = "nbg1", VolumeSizeGb = volumeGb,
        Currency = "USD", PricingUpdatedAt = Now.AddDays(-1), Vms = [.. vms]
    };
}
