using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Municode.Common.Contracts;
using Municode.Common.Data;
using Municode.Common.Models;
using Municode.Common.Providers;

namespace Municode.Common.Services;

/// <summary>
/// Read side of provider-reported charges. Values are recorded only by the worker; reading has no side effects and
/// nothing here is consulted by workspace create, start, stop or resize paths.
/// </summary>
public sealed class ProviderChargeService(WorkspaceDbContext db, TimeProvider clock, IOptions<ProviderChargesOptions> options)
{
    public const string Available = "available";
    public const string Stale = "stale";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";
    public const string NotRetrieved = "not_retrieved";
    public const string NotAvailableMessage = "Provider-reported charges not available";

    public async Task<BudgetResponse> AttachAsync(BudgetResponse budget, CancellationToken ct) =>
        (await AttachAsync(budget.OrganizationId, [budget], ct))[0];

    public async Task<IReadOnlyList<BudgetResponse>> AttachAsync(string organizationId, IReadOnlyList<BudgetResponse> budgets,
        CancellationToken ct)
    {
        if (budgets.Count == 0) return budgets;
        var period = Period(clock.GetUtcNow());
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(x => x.OrganizationId == organizationId).ToListAsync(ct);
        var charges = await db.ProviderCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Period == period).ToDictionaryAsync(x => x.ProviderAccountId, ct);
        return budgets.Select(budget => budget with { ProviderCharges = Summarize(budget, period, accounts, charges) }).ToList();
    }

    public static string Period(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private BudgetProviderChargesResponse Summarize(BudgetResponse budget, string period, IReadOnlyList<ProviderAccount> accounts,
        IReadOnlyDictionary<string, ProviderCharge> charges)
    {
        var scope = budget.ProviderAccountIds;
        var rows = accounts.Where(x => scope.Count == 0 || scope.Contains(x.Id))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(account => Row(account, charges.GetValueOrDefault(account.Id))).ToList();
        var totals = rows.Where(x => x.Amount is not null && x.Currency is not null)
            .GroupBy(x => x.Currency!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BudgetProviderChargeTotalResponse(x.Key.ToUpperInvariant(), x.Sum(r => r.Amount!.Value), x.Count()))
            .OrderBy(x => x.Currency, StringComparer.Ordinal).ToList();
        var complete = rows.Count > 0 && rows.All(x => x.Status == Available);
        List<string> notes =
        [
            "Provider-reported charges cover each whole provider account, including resources Municode does not manage, and are not part of the projection.",
            "Amounts are in each provider's billing currency. Different currencies are listed separately and never converted or summed."
        ];
        if (!complete && rows.Count > 0)
            notes.Add("Some accounts have no current provider-reported amount, so totals include only the amounts shown.");
        if (totals.Count > 0 && totals.Any(x => !string.Equals(x.Currency, budget.Currency, StringComparison.OrdinalIgnoreCase)))
            notes.Add($"This budget is in {budget.Currency}; amounts in other currencies are not compared with it.");
        return new(period, complete, rows, totals, notes);
    }

    private BudgetProviderChargeResponse Row(ProviderAccount account, ProviderCharge? charge)
    {
        var type = ProviderAccountService.WireType(account.ProviderType);
        if (!ProviderChargesSupport.IsSupported(account.ProviderType))
            return new(account.Id, account.Name, type, Unsupported, null, null, null, null, null,
                NotAvailableMessage + $": {account.ProviderType} has no billing API.");
        if (charge is null)
            return new(account.Id, account.Name, type, NotRetrieved, null, null, null, null, null,
                NotAvailableMessage + " yet: the worker has not retrieved this month's charges.");
        var hasAmount = charge.Amount is not null && charge.Currency is not null;
        string status, message;
        if (charge.Status == ProviderCharge.Failed)
        {
            status = Failed;
            message = $"The last refresh failed ({charge.LastError ?? "unknown error"}). "
                + (hasAmount ? "Showing the last reported amount." : NotAvailableMessage + ".");
        }
        else if (!hasAmount)
        {
            status = NotRetrieved;
            message = NotAvailableMessage + ".";
        }
        else if (charge.RetrievedAt is null || clock.GetUtcNow() - charge.RetrievedAt.Value > options.Value.StaleAfter)
        {
            status = Stale;
            message = $"Not refreshed in the last {options.Value.StaleAfter.TotalHours:0.#} hours; showing the last reported amount.";
        }
        else
        {
            status = Available;
            message = "Month-to-date charges reported by the provider.";
        }
        return new(account.Id, account.Name, type, status, hasAmount ? charge.Amount : null,
            hasAmount ? charge.Currency!.ToUpperInvariant() : null, hasAmount ? charge.AsOf : null, charge.AttemptedAt,
            charge.Source, message);
    }
}
