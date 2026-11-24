using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Models;

namespace Workspaces.Common.Services;

public sealed class BudgetException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>
/// Informational budgets: projections and threshold alerts only. Nothing here is consulted by
/// workspace create, start, stop or resize paths, and crossing a threshold never changes resources.
/// </summary>
public sealed class BudgetService(WorkspaceDbContext db, IIdGenerator ids, TimeProvider clock,
    IOptions<DevelopmentWorkspaceOptions> options, AuditEventService audit)
{
    public const string AlertActor = "system:budgets";
    public const decimal HoursPerMonth = 720m;
    public const int MaxBudgetsPerOrganization = 20;
    private static readonly int[] DefaultThresholds = [50, 80, 100];
    private DevelopmentWorkspaceOptions Policy => options.Value;
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IReadOnlyList<BudgetResponse>> ListAsync(string organizationId, CancellationToken ct)
    {
        var budgets = await db.Budgets.AsNoTracking().Where(x => x.OrganizationId == organizationId).ToListAsync(ct);
        if (budgets.Count == 0) return [];
        var inputs = await LoadInputsAsync(organizationId, ct);
        var responses = new List<BudgetResponse>(budgets.Count);
        foreach (var budget in budgets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            responses.Add(await EvaluateAsync(budget, inputs, ct));
        return responses;
    }

    public async Task<Budget> CreateAsync(CreateBudgetRequest request, string actor, CancellationToken ct)
    {
        var currency = (request.Currency ?? Policy.Currency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new BudgetException("budget_currency_invalid", "Use a three-letter ISO 4217 currency code.");
        var name = ValidName(request.Name);
        var thresholds = ValidThresholds(request.Thresholds);
        var amount = ValidAmount(request.MonthlyAmount);
        var accounts = await ValidAccountsAsync(request.OrganizationId, request.ProviderAccountIds, ct);
        if (await db.Budgets.CountAsync(x => x.OrganizationId == request.OrganizationId, ct) >= MaxBudgetsPerOrganization)
            throw new BudgetException("budget_limit_reached", $"An organization can have at most {MaxBudgetsPerOrganization} budgets.", 409);
        await EnsureNameFreeAsync(request.OrganizationId, name, null, ct);
        var budget = new Budget
        {
            Id = ids.NewId("budget"), OrganizationId = request.OrganizationId, Name = name, Currency = currency,
            MonthlyAmount = amount, ThresholdsJson = JsonSerializer.Serialize(thresholds),
            ProviderAccountIdsJson = JsonSerializer.Serialize(accounts), CreatedBy = actor, CreatedAt = Now, UpdatedAt = Now
        };
        db.Budgets.Add(budget);
        await db.SaveChangesAsync(ct);
        return budget;
    }

    public async Task<Budget> UpdateAsync(Budget budget, UpdateBudgetRequest request, CancellationToken ct)
    {
        if (budget.Revision != request.Revision)
            throw new BudgetException("revision_conflict", "This budget changed. Reload it before saving.", 409);
        var name = ValidName(request.Name);
        var thresholds = ValidThresholds(request.Thresholds);
        var amount = ValidAmount(request.MonthlyAmount);
        var accounts = await ValidAccountsAsync(budget.OrganizationId, request.ProviderAccountIds, ct);
        await EnsureNameFreeAsync(budget.OrganizationId, name, budget.Id, ct);
        budget.Name = name;
        budget.MonthlyAmount = amount;
        budget.ThresholdsJson = JsonSerializer.Serialize(thresholds);
        budget.ProviderAccountIdsJson = JsonSerializer.Serialize(accounts);
        // A changed amount, threshold or scope is a new alert baseline for the current period.
        budget.AlertPeriod = null;
        budget.AlertThresholdPercent = null;
        budget.AlertedAt = null;
        budget.UpdatedAt = Now;
        budget.Revision++;
        await db.SaveChangesAsync(ct);
        return budget;
    }

    public async Task DeleteAsync(Budget budget, CancellationToken ct)
    {
        db.Budgets.Remove(budget);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BudgetResponse> ProjectAsync(Budget budget, CancellationToken ct) =>
        await EvaluateAsync(budget, await LoadInputsAsync(budget.OrganizationId, ct), ct);

    private async Task<BudgetResponse> EvaluateAsync(Budget budget, Inputs inputs, CancellationToken ct)
    {
        var response = Project(budget, inputs);
        if (response.Projection.CrossedThreshold is { } threshold)
            await RecordAlertAsync(budget, response, threshold, ct);
        return response;
    }

    private BudgetResponse Project(Budget budget, Inputs inputs)
    {
        var thresholds = Thresholds(budget);
        var scope = AccountIds(budget);
        var period = Now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var staleBefore = Now.AddDays(-Math.Max(1, Policy.MaxPricingAgeDays));
        var contributors = new List<BudgetContributorResponse>();
        var excluded = new List<BudgetExcludedWorkspaceResponse>();
        var warnings = new List<string>();
        decimal computeHourly = 0, storageMonthly = 0;
        foreach (var workspace in inputs.Workspaces.Where(x => scope.Count == 0 || scope.Contains(x.ProviderAccountId)))
        {
            if (!string.Equals(workspace.Currency, budget.Currency, StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(new(workspace.Id, workspace.Name, workspace.Currency,
                    $"Priced in {workspace.Currency}; this budget is in {budget.Currency}. It is not converted or summed."));
                continue;
            }
            var reserved = workspace.Vms.Where(x => x.ComputeReserved).ToList();
            var hourly = reserved.Sum(x => Math.Max(0, x.ComputeHourlyRate));
            var storage = Math.Max(0, workspace.StorageMonthlyRate);
            var status = workspace.PricingUpdatedAt is null || reserved.Any(x => x.ComputeHourlyRate <= 0)
                || (workspace.VolumeSizeGb > 0 && workspace.StorageMonthlyRate <= 0) ? "unknown"
                : workspace.PricingUpdatedAt < staleBefore ? "stale" : "current";
            computeHourly += hourly;
            storageMonthly += storage;
            contributors.Add(new(workspace.Id, workspace.Name, workspace.ProviderAccountId, workspace.Region,
                reserved.Select(x => x.Sku).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                reserved.Count, Money(hourly, 4), Money(hourly * HoursPerMonth), Money(storage),
                Money(hourly * HoursPerMonth + storage), status, workspace.PricingUpdatedAt));
        }
        var unknown = contributors.Count(x => x.PricingStatus == "unknown");
        var stale = contributors.Count(x => x.PricingStatus == "stale");
        if (unknown > 0)
            warnings.Add($"{unknown} workspace(s) have missing pricing; their cost is unknown, so this projection is incomplete and may be too low.");
        if (stale > 0)
            warnings.Add($"{stale} workspace(s) use pricing older than {Math.Max(1, Policy.MaxPricingAgeDays)} days; actual provider prices may differ.");
        if (excluded.Count > 0)
            warnings.Add($"{excluded.Count} workspace(s) are priced in another currency and are excluded from this budget.");
        var missingScope = scope.Count(id => !inputs.Accounts.ContainsKey(id));
        if (missingScope > 0)
            warnings.Add($"{missingScope} scoped provider account(s) no longer exist.");
        var providers = contributors.GroupBy(x => x.ProviderAccountId).Select(group =>
        {
            inputs.Accounts.TryGetValue(group.Key, out var account);
            var compute = group.Sum(x => x.ComputeMonthly);
            var storageTotal = group.Sum(x => x.StorageMonthly);
            return new BudgetProviderProjectionResponse(group.Key, account?.Name ?? "Unknown provider account",
                account is null ? "unknown" : ProviderAccountService.WireType(account.ProviderType), compute, storageTotal,
                compute + storageTotal, group.Count(), group.Sum(x => x.ReservedVmCount));
        }).OrderByDescending(x => x.ProjectedMonthly).ThenBy(x => x.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
        var monthly = computeHourly * HoursPerMonth + storageMonthly;
        var percent = budget.MonthlyAmount > 0 ? monthly / budget.MonthlyAmount * 100m : 0;
        int? crossed = thresholds.Where(x => percent >= x).Select(x => (int?)x).Max();
        var projection = new BudgetProjectionResponse(period, true, unknown == 0, Money(computeHourly + storageMonthly / HoursPerMonth, 4),
            Money(monthly), Math.Round(percent, 1, MidpointRounding.AwayFromZero), crossed, monthly > budget.MonthlyAmount, false,
            providers, contributors.OrderByDescending(x => x.ProjectedMonthly).ThenBy(x => x.WorkspaceName, StringComparer.OrdinalIgnoreCase).ToList(),
            excluded, warnings, Assumptions(period));
        return new(budget.Id, budget.OrganizationId, budget.Name, budget.Currency, budget.MonthlyAmount, thresholds, scope.ToList(),
            budget.Revision, budget.CreatedAt, budget.UpdatedAt, projection);
    }

    // Claims the alert atomically so concurrent reads append at most one audit event per threshold per period.
    private async Task RecordAlertAsync(Budget budget, BudgetResponse response, int threshold, CancellationToken ct)
    {
        var period = response.Projection.Period;
        if (budget.AlertPeriod == period && budget.AlertThresholdPercent >= threshold) return;
        var now = Now;
        try
        {
            await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var claimed = await db.Budgets.Where(x => x.Id == budget.Id && x.Revision == budget.Revision
                    && (x.AlertPeriod == null || x.AlertPeriod != period || x.AlertThresholdPercent == null || x.AlertThresholdPercent < threshold))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.AlertPeriod, period)
                    .SetProperty(x => x.AlertThresholdPercent, threshold).SetProperty(x => x.AlertedAt, now), ct);
            if (claimed != 1) return;
            var projection = response.Projection;
            await audit.AppendAsync(budget.OrganizationId, AlertActor, "budget.threshold_crossed", JsonSerializer.Serialize(new
            {
                budgetId = budget.Id, budget = budget.Name, period, threshold, estimate = true, complete = projection.Complete,
                currency = budget.Currency, monthlyAmount = budget.MonthlyAmount, projectedMonthly = projection.ProjectedMonthly,
                percentOfBudget = projection.PercentOfBudget,
                contributors = projection.Contributors.Take(5).Select(x => new
                    { workspaceId = x.WorkspaceId, workspace = x.WorkspaceName, providerAccountId = x.ProviderAccountId, projectedMonthly = x.ProjectedMonthly })
            }), ct);
            if (tx is not null) await tx.CommitAsync(ct);
            budget.AlertPeriod = period;
            budget.AlertThresholdPercent = threshold;
            budget.AlertedAt = now;
        }
        catch (DbUpdateException)
        {
            // Alert bookkeeping is best-effort; the projection is still returned and will be re-evaluated on the next read.
            foreach (var entry in db.ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
    }

    private async Task<Inputs> LoadInputsAsync(string organizationId, CancellationToken ct)
    {
        var workspaces = await db.DevelopmentWorkspaces.AsNoTracking().Include(x => x.Vms)
            .Where(x => x.OrganizationId == organizationId && x.VolumeStatus != "deleted").ToListAsync(ct);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Id, ct);
        return new(workspaces, accounts);
    }

    private async Task EnsureNameFreeAsync(string organizationId, string name, string? exceptId, CancellationToken ct)
    {
        var names = await db.Budgets.Where(x => x.OrganizationId == organizationId && x.Id != exceptId).Select(x => x.Name).ToListAsync(ct);
        if (names.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
            throw new BudgetException("budget_name_taken", "A budget with this name already exists.", 409);
    }

    private async Task<List<string>> ValidAccountsAsync(string organizationId, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        var accounts = (requested ?? []).Select(x => x?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (accounts.Count == 0) return accounts;
        if (accounts.Count > 20 || accounts.Any(string.IsNullOrEmpty))
            throw new BudgetException("budget_scope_invalid", "Choose up to 20 provider accounts, or none for all providers.");
        var known = await db.ProviderAccounts.Where(x => x.OrganizationId == organizationId && accounts.Contains(x.Id)).CountAsync(ct);
        if (known != accounts.Count)
            throw new BudgetException("budget_scope_invalid", "Every scoped provider account must belong to this organization.");
        return accounts.Order(StringComparer.Ordinal).ToList();
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 1 or > 128 || trimmed.Any(char.IsControl))
            throw new BudgetException("budget_name_invalid", "Use a budget name of 1 to 128 characters without control characters.");
        return trimmed;
    }

    private static List<int> ValidThresholds(IReadOnlyList<int>? requested)
    {
        var thresholds = (requested is null || requested.Count == 0 ? DefaultThresholds : requested).Distinct().Order().ToList();
        if (thresholds.Count > 3 || thresholds.Any(x => x is < 1 or > 200))
            throw new BudgetException("budget_thresholds_invalid", "Use one to three alert thresholds between 1% and 200%.");
        return thresholds;
    }

    private static decimal ValidAmount(decimal amount)
    {
        if (amount <= 0 || amount > 1_000_000_000m || decimal.Round(amount, 2) != amount)
            throw new BudgetException("budget_amount_invalid", "Use a positive monthly amount with at most two decimal places.");
        return amount;
    }

    private static List<int> Thresholds(Budget budget) => JsonSerializer.Deserialize<List<int>>(budget.ThresholdsJson) ?? [];
    private static List<string> AccountIds(Budget budget) => JsonSerializer.Deserialize<List<string>>(budget.ProviderAccountIdsJson) ?? [];
    private static decimal Money(decimal value, int decimals = 2) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static List<string> Assumptions(string period) =>
    [
        $"Estimate for calendar month {period} (UTC), not an invoice or provider-reported charge.",
        $"Every VM currently reserving compute is assumed to keep running for a full {HoursPerMonth:0}-hour month at the hourly rate recorded when it started.",
        "Every retained workspace volume is charged at the monthly storage rate recorded when it was created.",
        "Rates come from operator-configured pricing, grouped by provider account; taxes, traffic, snapshots, backups, discounts and minimum charges are not included.",
        "Budgets only inform: crossing a threshold never blocks creating or starting workspaces and never stops resources."
    ];

    private sealed record Inputs(IReadOnlyList<DevelopmentWorkspace> Workspaces, IReadOnlyDictionary<string, ProviderAccount> Accounts);
}
