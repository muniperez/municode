using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Providers;
using Workspaces.Common.Services;

namespace Workspaces.Worker.Budgets;

/// <summary>
/// Records month-to-date provider-reported charges for provider accounts of organizations with budgets. Calls only
/// read-only billing endpoints, stores only aggregated amounts, and never creates, starts, stops or resizes resources.
/// </summary>
public sealed class ProviderChargesCollector(WorkspaceDbContext db, ProviderAccountClientFactory providers, IIdGenerator ids,
    TimeProvider clock, IOptions<ProviderChargesOptions> options, ILogger<ProviderChargesCollector> logger)
{
    // Returns the number of provider accounts whose billing endpoint was called.
    public async Task<int> RefreshDueAsync(CancellationToken ct)
    {
        var organizations = await db.Budgets.AsNoTracking().Select(x => x.OrganizationId).Distinct().ToListAsync(ct);
        if (organizations.Count == 0) return 0;
        var accounts = (await db.ProviderAccounts.AsNoTracking().Where(x => organizations.Contains(x.OrganizationId)).ToListAsync(ct))
            .Where(x => ProviderChargesSupport.IsSupported(x.ProviderType)).ToList();
        var accountIds = accounts.Select(x => x.Id).ToList();
        // SQLite cannot compare DateTimeOffset, so the latest attempt per account is found in memory.
        var lastAttempt = (await db.ProviderCharges.AsNoTracking().Where(x => accountIds.Contains(x.ProviderAccountId))
                .Select(x => new { x.ProviderAccountId, x.AttemptedAt }).ToListAsync(ct))
            .GroupBy(x => x.ProviderAccountId).ToDictionary(x => x.Key, x => x.Max(r => r.AttemptedAt));
        var refreshed = 0;
        foreach (var account in accounts.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (lastAttempt.TryGetValue(account.Id, out var attempted) && clock.GetUtcNow() - attempted < options.Value.Refresh) continue;
            await RefreshAsync(account, ct);
            refreshed++;
        }
        return refreshed;
    }

    private async Task RefreshAsync(ProviderAccount account, CancellationToken ct)
    {
        ProviderMonthToDateCharges? charges = null;
        string? error = null;
        try
        {
            if (await providers.CreateAsync(account.OrganizationId, account.Id, ct) is not IProviderChargesSource source)
                return;
            charges = await source.GetMonthToDateChargesAsync(ct);
            if (charges.Currency.Length != 3 || !charges.Currency.All(char.IsAsciiLetter))
                (charges, error) = (null, "The provider reported an unrecognised currency.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = Describe(exception);
            logger.LogWarning("Provider charges refresh failed for one account ({ErrorType}); resources were not changed.",
                exception.GetType().Name);
        }

        var now = clock.GetUtcNow();
        var period = ProviderChargeService.Period(charges?.AsOf ?? now);
        var row = await db.ProviderCharges.SingleOrDefaultAsync(x => x.OrganizationId == account.OrganizationId
            && x.ProviderAccountId == account.Id && x.Period == period, ct);
        if (row is null)
        {
            row = new ProviderCharge
            {
                Id = ids.NewId("provider_charge"), OrganizationId = account.OrganizationId, ProviderAccountId = account.Id,
                Period = period
            };
            db.ProviderCharges.Add(row);
        }
        row.AttemptedAt = now;
        if (charges is null)
        {
            // Keep the last reported amount so it can still be shown, labelled as failed.
            row.Status = ProviderCharge.Failed;
            row.LastError = error;
        }
        else
        {
            row.Status = ProviderCharge.Ok;
            row.Amount = charges.Amount;
            row.Currency = charges.Currency.ToUpperInvariant();
            row.Source = charges.Source;
            row.AsOf = charges.AsOf ?? now;
            row.RetrievedAt = now;
            row.LastError = null;
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another worker recorded this account and period first; its value is kept.
            db.ChangeTracker.Clear();
        }
    }

    // Only a status code or a fixed description is stored; provider response bodies are never kept.
    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http =>
            $"HTTP {(int)http.StatusCode!}: the provider credential cannot read billing data",
        HttpRequestException { StatusCode: not null } http => $"HTTP {(int)http.StatusCode!}",
        SecretStoreUnavailableException => "The provider credential is unavailable",
        ProviderCatalogException => "The provider account is unavailable",
        TaskCanceledException => "The provider did not respond in time",
        HttpRequestException => "The provider could not be reached",
        _ => "The provider response could not be read"
    };
}

public sealed class ProviderChargesWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<ProviderChargesOptions> options,
    ILogger<ProviderChargesWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ProviderChargesCollector>().RefreshDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Provider charges cycle failed ({ErrorType}); resources were not changed.", error.GetType().Name);
            }
            try { await Task.Delay(Poll < options.Value.Refresh ? Poll : options.Value.Refresh, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
