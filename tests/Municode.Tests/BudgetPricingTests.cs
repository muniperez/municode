using Municode.Common.Models;
using Municode.Common.Providers;
using Municode.Common.Services;

namespace Municode.Tests;

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

        var projection = BudgetProjector.Project(Budget("USD", 100m), [50, 100], BudgetScope.All, [capped, stale, fallback, missing],
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

        var usd = BudgetProjector.Project(Budget("USD", 10m), [100], BudgetScope.All, [mixed, euro, dollars], Accounts(), policy, Now);
        Assert.Equal(1m, usd.ProjectedMonthly);
        Assert.Equal(["dollars"], usd.Contributors.Select(x => x.WorkspaceId));
        Assert.Equal(("EUR, USD", "Priced in EUR and USD; this budget is in USD. It is not converted or summed."),
            (usd.Excluded.Single(x => x.WorkspaceId == "mixed").Currency, usd.Excluded.Single(x => x.WorkspaceId == "mixed").Reason));
        Assert.Equal("EUR", usd.Excluded.Single(x => x.WorkspaceId == "euro").Currency);

        var eur = BudgetProjector.Project(Budget("EUR", 10m), [100], BudgetScope.All, [mixed, euro, dollars], Accounts(), policy, Now);
        Assert.Equal(7.2m, eur.ProjectedMonthly);
        Assert.Equal(["euro"], eur.Contributors.Select(x => x.WorkspaceId));
        Assert.Equal(["dollars", "mixed"], eur.Excluded.Select(x => x.WorkspaceId).Order());
    }

    [Theory]
    [InlineData("month", "2026-11-30T23:59:59Z", "2026-11", "2026-11-01", "2026-12-01", "720")]
    [InlineData("month", "2026-12-01T00:00:00Z", "2026-12", "2026-12-01", "2027-01-01", "720")]
    [InlineData("week", "2026-11-29T23:59:59Z", "2026-W48", "2026-11-23", "2026-11-30", "168")]
    [InlineData("week", "2026-11-30T03:00:00+05:00", "2026-W48", "2026-11-23", "2026-11-30", "168")]
    [InlineData("week", "2026-11-30T00:00:00Z", "2026-W49", "2026-11-30", "2026-12-07", "24,144")]
    [InlineData("week", "2027-01-03T12:00:00Z", "2026-W53", "2026-12-28", "2027-01-04", "96,72")]
    [InlineData("week", "2027-01-04T00:00:00Z", "2027-W01", "2027-01-04", "2027-01-11", "168")]
    [InlineData("quarter", "2026-12-31T23:59:59Z", "2026-Q4", "2026-10-01", "2027-01-01", "720,720,720")]
    [InlineData("quarter", "2027-01-01T00:00:00Z", "2027-Q1", "2027-01-01", "2027-04-01", "720,720,720")]
    public void PeriodsAreUtcCalendarMonthsIsoWeeksAndQuartersSplitIntoBillingMonths(string type, string at, string key,
        string start, string end, string segments)
    {
        var window = BudgetPeriods.For(type, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((key, DateTimeOffset.Parse(start + "T00:00:00Z"), DateTimeOffset.Parse(end + "T00:00:00Z")),
            (window.Key, window.Start, window.End));
        Assert.Equal(segments.Split(',').Select(decimal.Parse), window.MonthSegments.Select(x => (decimal)x.TotalHours));
        Assert.True(window.Key.Length <= BudgetPeriods.MaxKeyLength);
    }

    [Fact]
    public void RecentPeriodKeysCoverTheCurrentAndPreviousPeriodOfEachType() =>
        Assert.Equal(["2027-01", "2026-12", "2026-W53", "2026-W52", "2027-Q1", "2026-Q4"],
            BudgetPeriods.RecentKeys(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void PeriodProjectionsApplyCapsAndMinimumChargesPerCalendarMonthAndCompareThePeriodAmount()
    {
        var policy = Policy(new()
        {
            ["capped"] = Entry(BudgetPricing.HourlyMonthlyCap, hourly: 1m, monthly: 50m, sku: "cx22", currency: "USD"),
            ["plain"] = Entry(BudgetPricing.Hourly, hourly: .2m, sku: "cx32", currency: "USD"),
            ["minimum"] = Entry(BudgetPricing.PerSecond, hourly: .01m, minimumCharge: 30m, sku: "cx42", currency: "USD")
        });
        var stored = Workspace("stored", 10);
        stored.StorageMonthlyRate = 2m;
        IReadOnlyList<DevelopmentWorkspace> workspaces = [Workspace("capped", 0, Vm("capped-vm", "cx22", .1m)),
            Workspace("plain", 0, Vm("plain-vm", "cx32", .1m)), Workspace("minimum", 0, Vm("minimum-vm", "cx42", .1m)), stored];
        // Wednesday 2 December: ISO week 49 runs 30 November to 6 December, so 24 hours in November and 144 in December.
        var at = new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.Zero);

        var weekly = BudgetProjector.Project(Budget("USD", 100m, BudgetPeriods.Week), [50, 100], BudgetScope.All, workspaces,
            Accounts(), policy, at);
        var lines = weekly.Contributors.SelectMany(x => x.Lines).ToDictionary(x => x.ResourceId);
        Assert.Equal((74m, 50m), (lines["capped-vm"].PeriodAmount, lines["capped-vm"].Amount));
        Assert.Equal((33.6m, 144m), (lines["plain-vm"].PeriodAmount, lines["plain-vm"].Amount));
        Assert.Equal((60m, 30m), (lines["minimum-vm"].PeriodAmount, lines["minimum-vm"].Amount));
        Assert.Equal((.47m, 2m), (lines["stored"].PeriodAmount, lines["stored"].Amount));
        Assert.Equal(("2026-W49", "week", 168m, 168.07m, 226m), (weekly.Period, weekly.PeriodType, weekly.PeriodHours,
            weekly.ProjectedAmount, weekly.ProjectedMonthly));
        Assert.Equal((168.1m, 100, true), (weekly.PercentOfBudget, weekly.CrossedThreshold, weekly.OverBudget));
        Assert.Equal(168.07m, Assert.Single(weekly.Providers).ProjectedAmount);
        Assert.Contains(weekly.Assumptions, x => x.StartsWith("Estimate for ISO week 2026-W49 (2026-11-30 to 2026-12-06, UTC)", StringComparison.Ordinal));
        Assert.Contains(weekly.Assumptions, x => x.Contains("monthly caps and minimum charges apply separately in each calendar month", StringComparison.Ordinal));

        var quarterly = BudgetProjector.Project(Budget("USD", 1000m, BudgetPeriods.Quarter), [50, 100], BudgetScope.All,
            workspaces, Accounts(), policy, at);
        Assert.Equal(("2026-Q4", 2160m, 678m, 226m, 50), (quarterly.Period, quarterly.PeriodHours, quarterly.ProjectedAmount,
            quarterly.ProjectedMonthly, quarterly.CrossedThreshold));

        var monthly = BudgetProjector.Project(Budget("USD", 1000m), [50, 100], BudgetScope.All, workspaces, Accounts(), policy, at);
        Assert.Equal(("2026-12", 720m, 226m, 226m), (monthly.Period, monthly.PeriodHours, monthly.ProjectedAmount, monthly.ProjectedMonthly));
        Assert.Contains(monthly.Assumptions, x => x.StartsWith("Estimate for calendar month 2026-12 (2026-12-01 to 2026-12-31, UTC)", StringComparison.Ordinal));
    }

    [Fact]
    public void ScopeSelectsWholeWorkspacesOrIndividualVmsWithinTheScopedAccounts()
    {
        var a = Workspace("a", 10, Vm("a-1", "cx22", .1m), Vm("a-2", "cx22", .1m));
        var b = Workspace("b", 10, Vm("b-1", "cx22", .1m), Vm("b-2", "cx22", .1m, reserved: false));
        var c = Workspace("c", 10, Vm("c-1", "cx22", .1m));
        c.ProviderAccountId = "other";
        foreach (var workspace in new[] { a, b, c }) workspace.StorageMonthlyRate = 2m;
        IReadOnlyList<DevelopmentWorkspace> workspaces = [a, b, c];
        var policy = Policy(new());

        var selected = BudgetProjector.Project(Budget("USD", 1000m), [100],
            new BudgetScope([], ["a", "gone"], ["b-1", "c-1", "vm-gone"]), workspaces, Accounts(), policy, Now);
        Assert.Equal(new Dictionary<string, decimal> { ["a"] = 146m, ["b"] = 72m, ["c"] = 72m },
            selected.Contributors.ToDictionary(x => x.WorkspaceId, x => x.ProjectedAmount));
        Assert.Equal(["b-1"], selected.Contributors.Single(x => x.WorkspaceId == "b").Lines.Select(x => x.ResourceId));
        Assert.Equal(290m, selected.ProjectedAmount);
        Assert.Contains("1 selected workspace(s) no longer exist.", selected.Warnings);
        Assert.Contains("1 selected VM(s) no longer exist.", selected.Warnings);
        Assert.Contains(selected.Assumptions, x => x.StartsWith("Only the selected workspaces", StringComparison.Ordinal));

        var narrowed = BudgetProjector.Project(Budget("USD", 1000m), [100],
            new BudgetScope(["account"], ["a"], ["b-1", "c-1"]), workspaces, Accounts(), policy, Now);
        Assert.Equal((218m, 2), (narrowed.ProjectedAmount, narrowed.Contributors.Count));

        var stopped = BudgetProjector.Project(Budget("USD", 1000m), [100], new BudgetScope([], [], ["b-2"]), workspaces,
            Accounts(), policy, Now);
        var only = Assert.Single(stopped.Contributors);
        Assert.Equal(("b", 0, 0m, 0), (only.WorkspaceId, only.ReservedVmCount, only.ProjectedAmount, only.Lines.Count));

        var everything = BudgetProjector.Project(Budget("USD", 1000m), [100], BudgetScope.All, workspaces, Accounts(), policy, Now);
        Assert.Equal(146m + 74m + 74m, everything.ProjectedAmount);
        Assert.DoesNotContain(everything.Assumptions, x => x.StartsWith("Only the selected", StringComparison.Ordinal));
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

    private static Budget Budget(string currency, decimal amount, string period = BudgetPeriods.Month) => new()
        { Id = "budget", OrganizationId = "org", Name = "Budget", Currency = currency, Period = period, Amount = amount, CreatedBy = "test" };

    private static Dictionary<string, ProviderAccount> Accounts() => new()
    {
        ["account"] = new() { Id = "account", OrganizationId = "org", Name = "Hetzner", ProviderType = CloudProvider.Hetzner,
            CredentialPath = "synthetic", CredentialGeneration = "synthetic" },
        ["other"] = new() { Id = "other", OrganizationId = "org", Name = "Hetzner other", ProviderType = CloudProvider.Hetzner,
            CredentialPath = "synthetic", CredentialGeneration = "synthetic" }
    };

    private static DevelopmentWorkspaceVm Vm(string id, string sku, decimal rate, bool reserved = true) => new()
        { Id = id, Kind = DevelopmentWorkspaceVmKinds.Primary, Region = "nbg1", Sku = sku, ComputeHourlyRate = rate, ComputeReserved = reserved };

    private static DevelopmentWorkspace Workspace(string id, int volumeGb, params DevelopmentWorkspaceVm[] vms) => new()
    {
        Id = id, Name = id, OrganizationId = "org", ProviderAccountId = "account", Region = "nbg1", VolumeSizeGb = volumeGb,
        Currency = "USD", PricingUpdatedAt = Now.AddDays(-1), Vms = [.. vms]
    };
}
