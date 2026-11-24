using System.Globalization;
using Workspaces.Common.Providers;

namespace Workspaces.Common.Services;

/// <summary>
/// One operator-configured list price (<c>DevelopmentWorkspaces:PriceCatalog:&lt;id&gt;</c>). Budgets use it for estimates
/// only; it is never fetched from provider APIs and never read by create, start, stop or resize paths.
/// </summary>
public sealed class BudgetPriceCatalogEntry
{
    /// <summary>Provider type: <c>hetzner</c>, <c>digital-ocean</c> or <c>vultr</c>.</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>Optional provider account ID; a matching account entry wins over a provider-wide entry.</summary>
    public string? ProviderAccountId { get; set; }
    /// <summary><c>compute</c> (priced per VM) or <c>storage</c> (priced per GB of workspace volume).</summary>
    public string Resource { get; set; } = BudgetPricing.Compute;
    /// <summary>Optional region; empty matches every region, and a matching region wins over an empty one.</summary>
    public string? Region { get; set; }
    /// <summary>Provider size or SKU. Required for compute and ignored for storage.</summary>
    public string? Sku { get; set; }
    public string BillingModel { get; set; } = BudgetPricing.Hourly;
    /// <summary>Price per unit-hour for <c>hourly</c>, <c>hourly_monthly_cap</c> and <c>per_second</c>.</summary>
    public decimal? HourlyRate { get; set; }
    /// <summary>Monthly cap per unit for <c>hourly_monthly_cap</c>, or the monthly price per unit for <c>monthly</c>.</summary>
    public decimal? MonthlyRate { get; set; }
    /// <summary>Smallest charge per resource for each billing month in which it is billed at all.</summary>
    public decimal? MinimumCharge { get; set; }
    /// <summary>Smallest billed duration per resource for each billing month in which it is billed at all.</summary>
    public int? MinimumBillableSeconds { get; set; }
    public string Currency { get; set; } = string.Empty;
    /// <summary>Where the operator took the price from, for example a dated provider price page.</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>When the price takes effect. Entries in the future are ignored until then.</summary>
    public DateTimeOffset? EffectiveFrom { get; set; }
}

public sealed record BudgetCatalogMatch(string Id, BudgetPriceCatalogEntry Entry);

/// <summary>Catalog lookup and billing arithmetic for budget estimates.</summary>
public static class BudgetPricing
{
    public const string Compute = "compute";
    public const string Storage = "storage";
    public const string Hourly = "hourly";
    public const string HourlyMonthlyCap = "hourly_monthly_cap";
    public const string PerSecond = "per_second";
    public const string Monthly = "monthly";
    public const decimal HoursPerMonth = 720m;
    private static readonly string[] Providers = ["hetzner", "digital-ocean", "vultr"];
    private static readonly string[] Models = [Hourly, HourlyMonthlyCap, PerSecond, Monthly];

    /// <summary>Returns why an entry cannot be used, or null when it is valid.</summary>
    public static string? Invalid(BudgetPriceCatalogEntry entry)
    {
        if (!Providers.Contains(entry.Provider, StringComparer.Ordinal)) return "provider must be hetzner, digital-ocean or vultr";
        if (entry.Resource is not (Compute or Storage)) return "resource must be compute or storage";
        if (entry.Resource == Compute && string.IsNullOrWhiteSpace(entry.Sku)) return "compute entries need a SKU";
        if (!Models.Contains(entry.BillingModel, StringComparer.Ordinal))
            return "billing model must be hourly, hourly_monthly_cap, per_second or monthly";
        if (entry.BillingModel is Hourly or HourlyMonthlyCap or PerSecond && entry.HourlyRate is not > 0)
            return "hourly rate must be positive";
        if (entry.BillingModel is HourlyMonthlyCap or Monthly && entry.MonthlyRate is not > 0)
            return "monthly rate must be positive";
        if (entry.MinimumCharge is < 0 || entry.MinimumBillableSeconds is < 0) return "minimums cannot be negative";
        if (entry.Currency.Length != 3 || !entry.Currency.All(char.IsAsciiLetterUpper)) return "currency must be a three-letter ISO 4217 code";
        if (string.IsNullOrWhiteSpace(entry.Source)) return "source is required";
        if (entry.EffectiveFrom is null) return "effective date is required";
        return null;
    }

    /// <summary>
    /// Most specific valid entry in effect at <paramref name="now"/>: account beats provider-wide, region beats any region,
    /// then the latest effective date. Returns null when no entry applies.
    /// </summary>
    public static BudgetCatalogMatch? Find(IReadOnlyDictionary<string, BudgetPriceCatalogEntry> catalog, CloudProvider provider,
        string providerAccountId, string resource, string region, string? sku, DateTimeOffset now)
    {
        var type = ProviderAccountService.WireType(provider);
        return catalog.Where(x => Invalid(x.Value) is null && x.Value.Provider == type && x.Value.Resource == resource
                && x.Value.EffectiveFrom <= now
                && (string.IsNullOrEmpty(x.Value.ProviderAccountId) || x.Value.ProviderAccountId == providerAccountId)
                && (string.IsNullOrEmpty(x.Value.Region) || string.Equals(x.Value.Region, region, StringComparison.OrdinalIgnoreCase))
                && (resource == Storage || string.Equals(x.Value.Sku, sku, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => !string.IsNullOrEmpty(x.Value.ProviderAccountId))
            .ThenByDescending(x => !string.IsNullOrEmpty(x.Value.Region))
            .ThenByDescending(x => x.Value.EffectiveFrom)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new BudgetCatalogMatch(x.Key, x.Value)).FirstOrDefault();
    }

    /// <summary>
    /// Charge for <paramref name="quantity"/> units (one VM, or GB of storage) used for <paramref name="usage"/> within one
    /// billing month. Hourly models bill every started hour, per-second bills every started second, monthly prorates by
    /// <see cref="HoursPerMonth"/>. The monthly cap and minimum charge apply per resource per billing month.
    /// </summary>
    public static decimal Charge(BudgetPriceCatalogEntry entry, decimal quantity, TimeSpan usage)
    {
        if (usage <= TimeSpan.Zero || quantity <= 0) return 0;
        var seconds = Math.Max((decimal)Math.Ceiling(usage.TotalSeconds), entry.MinimumBillableSeconds ?? 0);
        var hours = seconds / 3600m;
        var perUnit = entry.BillingModel switch
        {
            Hourly => entry.HourlyRate!.Value * Math.Ceiling(hours),
            HourlyMonthlyCap => Math.Min(entry.HourlyRate!.Value * Math.Ceiling(hours), entry.MonthlyRate!.Value),
            PerSecond => entry.HourlyRate!.Value * hours,
            Monthly => entry.MonthlyRate!.Value * Math.Min(hours, HoursPerMonth) / HoursPerMonth,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.BillingModel, "Unknown billing model.")
        };
        return Math.Max(perUnit * quantity, entry.MinimumCharge ?? 0);
    }

    /// <summary>A short, user-facing statement of how an entry is billed.</summary>
    public static string Describe(string id, BudgetPriceCatalogEntry entry)
    {
        var unit = entry.Resource == Storage ? "GB" : "VM";
        var what = entry.Resource == Storage ? "Storage" : $"Compute {entry.Sku}";
        var where = string.IsNullOrEmpty(entry.Region) ? "all regions" : entry.Region;
        var rate = entry.BillingModel switch
        {
            Hourly => $"billed per started hour at {Rate(entry.HourlyRate)} {entry.Currency}/{unit}-hour",
            HourlyMonthlyCap => $"billed per started hour at {Rate(entry.HourlyRate)} {entry.Currency}/{unit}-hour, capped at {Rate(entry.MonthlyRate)} {entry.Currency}/{unit} per month",
            PerSecond => $"billed per second at {Rate(entry.HourlyRate)} {entry.Currency}/{unit}-hour",
            _ => $"billed monthly at {Rate(entry.MonthlyRate)} {entry.Currency}/{unit}, prorated by hour"
        };
        var minimums = new List<string>();
        if (entry.MinimumBillableSeconds is > 0) minimums.Add($"at least {entry.MinimumBillableSeconds} s billed");
        if (entry.MinimumCharge is > 0) minimums.Add($"minimum charge {Rate(entry.MinimumCharge)} {entry.Currency} per month");
        var minimum = minimums.Count == 0 ? "" : $" ({string.Join(", ", minimums)})";
        return $"{what} in {where}: {rate}{minimum}. Catalog entry {id}, source {entry.Source}, effective "
            + $"{entry.EffectiveFrom!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
    }

    private static string Rate(decimal? value) => (value ?? 0).ToString("0.00##", CultureInfo.InvariantCulture);
}
