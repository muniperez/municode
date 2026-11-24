using Workspaces.Common.Contracts;
using Workspaces.Common.Models;

namespace Workspaces.Common.Services;

/// <summary>
/// Pure budget projection: prices each reserved VM and retained volume from the price catalog, falling back to the rate
/// recorded on the workspace. Missing prices are flagged and left out of totals rather than counted as zero, and amounts in
/// another currency are never summed.
/// </summary>
/// <summary>
/// Which resources a budget counts. Provider accounts narrow the scope; selected workspaces (all reserved VMs and the
/// volume) and individual VMs (compute only) narrow it further. Empty lists select everything.
/// </summary>
public sealed record BudgetScope(IReadOnlyCollection<string> ProviderAccountIds, IReadOnlyCollection<string> WorkspaceIds,
    IReadOnlyCollection<string> VmIds)
{
    public static readonly BudgetScope All = new([], [], []);
    public bool SelectsResources => WorkspaceIds.Count > 0 || VmIds.Count > 0;
}

public static class BudgetProjector
{
    public const string Catalog = "catalog";
    public const string Fallback = "fallback";
    public const string Current = "current";
    public const string Stale = "stale";
    public const string Missing = "missing";

    public static BudgetProjectionResponse Project(Budget budget, IReadOnlyList<int> thresholds, BudgetScope scope,
        IReadOnlyList<DevelopmentWorkspace> workspaces, IReadOnlyDictionary<string, ProviderAccount> accounts,
        DevelopmentWorkspaceOptions policy, DateTimeOffset now)
    {
        var window = BudgetPeriods.For(BudgetPeriods.IsValid(budget.Period) ? budget.Period : BudgetPeriods.Month, now);
        var hours = BudgetPricing.HoursPerMonth;
        var pricing = new Pricing(policy, now, window);
        var contributors = new List<BudgetContributorResponse>();
        var excluded = new List<BudgetExcludedWorkspaceResponse>();
        var warnings = new List<string>();
        foreach (var workspace in workspaces.Where(x => scope.ProviderAccountIds.Count == 0 || scope.ProviderAccountIds.Contains(x.ProviderAccountId)))
        {
            var whole = !scope.SelectsResources || scope.WorkspaceIds.Contains(workspace.Id);
            if (!whole && !workspace.Vms.Any(x => scope.VmIds.Contains(x.Id))) continue;
            accounts.TryGetValue(workspace.ProviderAccountId, out var account);
            var reserved = workspace.Vms.Where(x => x.ComputeReserved && (whole || scope.VmIds.Contains(x.Id)))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            var lines = reserved.Select(vm => pricing.Compute(workspace, vm, account)).ToList();
            if (whole && workspace.VolumeSizeGb > 0) lines.Add(pricing.Storage(workspace, account));
            var currencies = (lines.Count == 0 ? new[] { workspace.Currency } : lines.Select(x => x.Currency))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
            if (currencies.Any(x => !string.Equals(x, budget.Currency, StringComparison.OrdinalIgnoreCase)))
            {
                excluded.Add(new(workspace.Id, workspace.Name, string.Join(", ", currencies),
                    $"Priced in {string.Join(" and ", currencies)}; this budget is in {budget.Currency}. It is not converted or summed."));
                continue;
            }
            var compute = lines.Where(x => x.Resource == BudgetPricing.Compute).Sum(x => x.Amount ?? 0);
            var storage = lines.Where(x => x.Resource == BudgetPricing.Storage).Sum(x => x.Amount ?? 0);
            var status = lines.Any(x => x.Status == Missing) ? "unknown" : lines.Any(x => x.Status == Stale) ? Stale : Current;
            contributors.Add(new(workspace.Id, workspace.Name, workspace.ProviderAccountId, workspace.Region,
                reserved.Select(x => x.Sku).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                reserved.Count, Money(compute / hours, 4), Money(compute), Money(storage), Money(compute + storage), status,
                workspace.PricingUpdatedAt, lines, Money(lines.Sum(x => x.PeriodAmount ?? 0))));
        }
        var unknown = contributors.Count(x => x.PricingStatus == "unknown");
        var stale = contributors.Count(x => x.PricingStatus == Stale);
        if (unknown > 0)
            warnings.Add($"{unknown} workspace(s) have missing pricing; their cost is unknown, so this projection is incomplete and may be too low.");
        if (stale > 0)
            warnings.Add($"{stale} workspace(s) use pricing of unknown age or older than the operator's limit ({pricing.FallbackMaxAgeDays} days "
                + $"for recorded rates, {pricing.CatalogMaxAgeDays} days for price catalog entries); actual provider prices may differ.");
        if (excluded.Count > 0)
            warnings.Add($"{excluded.Count} workspace(s) are priced in another currency and are excluded from this budget.");
        var missingScope = scope.ProviderAccountIds.Count(id => !accounts.ContainsKey(id));
        if (missingScope > 0)
            warnings.Add($"{missingScope} scoped provider account(s) no longer exist.");
        var missingWorkspaces = scope.WorkspaceIds.Count(id => workspaces.All(x => x.Id != id));
        if (missingWorkspaces > 0)
            warnings.Add($"{missingWorkspaces} selected workspace(s) no longer exist.");
        var missingVms = scope.VmIds.Count(id => workspaces.All(x => x.Vms.All(vm => vm.Id != id)));
        if (missingVms > 0)
            warnings.Add($"{missingVms} selected VM(s) no longer exist.");
        var invalid = policy.PriceCatalog.Select(x => (x.Key, Reason: BudgetPricing.Invalid(x.Value))).Where(x => x.Reason is not null)
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        if (invalid.Count > 0)
            warnings.Add($"{invalid.Count} price catalog entr{(invalid.Count == 1 ? "y is" : "ies are")} invalid and ignored: "
                + string.Join("; ", invalid.Select(x => $"{x.Key} ({x.Reason})")) + ".");
        var providers = contributors.GroupBy(x => x.ProviderAccountId).Select(group =>
        {
            accounts.TryGetValue(group.Key, out var account);
            var computeTotal = group.Sum(x => x.ComputeMonthly);
            var storageTotal = group.Sum(x => x.StorageMonthly);
            return new BudgetProviderProjectionResponse(group.Key, account?.Name ?? "Unknown provider account",
                account is null ? "unknown" : ProviderAccountService.WireType(account.ProviderType), computeTotal, storageTotal,
                computeTotal + storageTotal, group.Count(), group.Sum(x => x.ReservedVmCount),
                group.All(x => x.PricingStatus != "unknown"), pricing.Assumptions(group.SelectMany(x => x.Lines).ToList(), window.Hours),
                group.Sum(x => x.ProjectedAmount));
        }).OrderByDescending(x => x.ProjectedAmount).ThenBy(x => x.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
        var monthly = contributors.Sum(x => x.ProjectedMonthly);
        var projected = contributors.Sum(x => x.ProjectedAmount);
        var percent = budget.Amount > 0 ? projected / budget.Amount * 100m : 0;
        int? crossed = thresholds.Where(x => percent >= x).Select(x => (int?)x).Max();
        return new BudgetProjectionResponse(window.Key, true, unknown == 0, Money(monthly / hours, 4), Money(monthly),
            Math.Round(percent, 1, MidpointRounding.AwayFromZero), crossed, projected > budget.Amount, false, providers,
            contributors.OrderByDescending(x => x.ProjectedAmount).ThenBy(x => x.WorkspaceName, StringComparer.OrdinalIgnoreCase).ToList(),
            excluded, warnings, Assumptions(window, scope), window.Type, window.Start, window.End, window.Hours, projected);
    }

    private static decimal Money(decimal value, int decimals = 2) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static List<string> Assumptions(BudgetPeriodWindow window, BudgetScope scope)
    {
        var result = new List<string>
        {
            $"Estimate for {BudgetPeriods.Describe(window)}, not an invoice or provider-reported charge.",
            window.Type == BudgetPeriods.Month
                ? $"Every VM currently reserving compute is assumed to keep running for a full {BudgetPricing.HoursPerMonth:0}-hour month, and every retained workspace volume is assumed to be kept all month."
                : $"Every VM currently reserving compute is assumed to keep running for the whole period ({window.Hours:0.##} hours), and every retained workspace volume is assumed to be kept the whole period. A full calendar month counts as {BudgetPricing.HoursPerMonth:0} hours; monthly caps and minimum charges apply separately in each calendar month the period touches. Monthly amounts are shown for comparison."
        };
        if (scope.SelectsResources)
            result.Add("Only the selected workspaces (all their VMs and their volume) and individually selected VMs (compute only) are counted.");
        result.AddRange(CommonAssumptions);
        return result;
    }

    private static readonly string[] CommonAssumptions =
    [
        "Prices come from the operator's price catalog for each provider, region and SKU, including hourly billing, monthly caps, per-second billing and minimum charges. Without a catalog entry, the rate recorded when the VM was selected or the workspace was created is used as a fallback rate. Each provider lists its pricing assumptions.",
        "A resource with no catalog entry and no recorded rate has a missing price: it is flagged and not counted, never treated as zero. Amounts in another currency are never converted or summed.",
        "Taxes, traffic, snapshots, backups and discounts are not included.",
        "Budgets only inform: crossing a threshold never blocks creating or starting workspaces and never stops resources."
    ];

    private sealed class Pricing(DevelopmentWorkspaceOptions policy, DateTimeOffset now, BudgetPeriodWindow window)
    {
        private static readonly TimeSpan Month = TimeSpan.FromHours((double)BudgetPricing.HoursPerMonth);

        public int FallbackMaxAgeDays { get; } = Math.Max(1, policy.MaxPricingAgeDays);
        public int CatalogMaxAgeDays { get; } = Math.Max(1, policy.PriceCatalogMaxAgeDays);

        public BudgetPriceLineResponse Compute(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, ProviderAccount? account)
        {
            var match = account is null ? null : BudgetPricing.Find(policy.PriceCatalog, account.ProviderType, account.Id,
                BudgetPricing.Compute, vm.Region, vm.Sku, now);
            if (match is not null) return FromCatalog(BudgetPricing.Compute, vm.Id, vm.Sku, 1, match);
            return FromFallback(BudgetPricing.Compute, vm.Id, vm.Sku, 1, BudgetPricing.Hourly, workspace, vm.ComputeHourlyRate,
                usage => vm.ComputeHourlyRate * (decimal)usage.TotalHours);
        }

        public BudgetPriceLineResponse Storage(DevelopmentWorkspace workspace, ProviderAccount? account)
        {
            var match = account is null ? null : BudgetPricing.Find(policy.PriceCatalog, account.ProviderType, account.Id,
                BudgetPricing.Storage, workspace.Region, null, now);
            if (match is not null) return FromCatalog(BudgetPricing.Storage, workspace.Id, null, workspace.VolumeSizeGb, match);
            return FromFallback(BudgetPricing.Storage, workspace.Id, null, workspace.VolumeSizeGb, BudgetPricing.Monthly, workspace,
                workspace.StorageMonthlyRate,
                usage => workspace.StorageMonthlyRate * Math.Min((decimal)usage.TotalHours, BudgetPricing.HoursPerMonth) / BudgetPricing.HoursPerMonth);
        }

        public List<string> Assumptions(IReadOnlyList<BudgetPriceLineResponse> lines, decimal hours)
        {
            var result = new List<string>();
            foreach (var group in lines.Where(x => x.Source == Catalog).GroupBy(x => x.CatalogEntryId!).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var entry = policy.PriceCatalog[group.Key];
                var text = BudgetPricing.Describe(group.Key, entry);
                if (group.Any(x => x.Status == Stale)) text += $" This price is older than {CatalogMaxAgeDays} days and may be outdated.";
                result.Add(text);
            }
            var compute = lines.Count(x => x.Source == Fallback && x.Resource == BudgetPricing.Compute && x.Status != Missing);
            if (compute > 0)
                result.Add($"Fallback rate: {compute} VM(s) have no catalog entry for their region and SKU, so the hourly rate recorded when each VM was selected is assumed for {hours:0.##} hours.");
            var storage = lines.Count(x => x.Source == Fallback && x.Resource == BudgetPricing.Storage && x.Status != Missing);
            if (storage > 0)
                result.Add($"Fallback rate: {storage} volume(s) have no storage catalog entry, so the monthly storage rate recorded when the workspace was created is used.");
            var missing = lines.Count(x => x.Status == Missing);
            if (missing > 0)
                result.Add($"Missing price: {missing} resource(s) have neither a catalog entry nor a recorded rate; their cost is unknown and not counted.");
            return result;
        }

        private BudgetPriceLineResponse FromCatalog(string resource, string id, string? sku, decimal quantity, BudgetCatalogMatch match)
        {
            var entry = match.Entry;
            var status = entry.EffectiveFrom < now.AddDays(-CatalogMaxAgeDays) ? Stale : Current;
            return new(resource, id, sku, quantity, Catalog, match.Id, entry.BillingModel, entry.Currency.ToUpperInvariant(),
                Money(BudgetPricing.Charge(entry, quantity, Month)), status, entry.EffectiveFrom,
                Money(window.MonthSegments.Sum(segment => BudgetPricing.Charge(entry, quantity, segment))));
        }

        private BudgetPriceLineResponse FromFallback(string resource, string id, string? sku, decimal quantity, string model,
            DevelopmentWorkspace workspace, decimal rate, Func<TimeSpan, decimal> charge)
        {
            var missing = rate <= 0;
            var status = missing ? Missing
                : workspace.PricingUpdatedAt is null || workspace.PricingUpdatedAt < now.AddDays(-FallbackMaxAgeDays) ? Stale : Current;
            return new(resource, id, sku, quantity, Fallback, null, model, workspace.Currency.ToUpperInvariant(),
                missing ? null : Money(charge(Month)), status, workspace.PricingUpdatedAt,
                missing ? null : Money(window.MonthSegments.Sum(charge)));
        }
    }
}
