using System.Net;
using System.Security.Cryptography;
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

/// <summary>Saved budget plus a signing secret to reveal once and a retired secret to delete after commit.</summary>
public sealed record BudgetChange(Budget Budget, string? WebhookSigningSecret, StoredSecretReference? RetiredWebhookSecret);

/// <summary>
/// Informational budgets: projections and threshold alerts only. Nothing here is consulted by
/// workspace create, start, stop or resize paths, and crossing a threshold never changes resources.
/// Reads are side-effect free; <see cref="EvaluateAlertsAsync"/> is only called by the background worker.
/// </summary>
public sealed class BudgetService(WorkspaceDbContext db, IIdGenerator ids, TimeProvider clock,
    IOptions<DevelopmentWorkspaceOptions> options, AuditEventService audit, ISecretStore secrets,
    IOptions<BudgetEmailOptions> email)
{
    public const string AlertActor = "system:budgets";
    public const string ThresholdCrossedEvent = "budget.threshold_crossed";
    public const decimal HoursPerMonth = 720m;
    public const int MaxBudgetsPerOrganization = 20;
    public const int MaxScopedResources = 50;
    private const int PayloadContributors = 10;
    private static readonly int[] DefaultThresholds = [50, 80, 100];
    private DevelopmentWorkspaceOptions Policy => options.Value;
    private bool EmailAvailable => email.Value.IsConfigured;
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<IReadOnlyList<BudgetResponse>> ListAsync(string organizationId, CancellationToken ct)
    {
        var budgets = await db.Budgets.AsNoTracking().Where(x => x.OrganizationId == organizationId).ToListAsync(ct);
        if (budgets.Count == 0) return [];
        var inputs = await LoadInputsAsync(organizationId, ct);
        var deliveries = await LoadDeliveriesAsync(budgets.Select(x => x.Id).ToList(), ct);
        return budgets.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(budget => Respond(budget, Project(budget, inputs), deliveries, EmailAvailable)).ToList();
    }

    public async Task<BudgetChange> CreateAsync(CreateBudgetRequest request, string actor, CancellationToken ct)
    {
        var currency = (request.Currency ?? Policy.Currency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new BudgetException("budget_currency_invalid", "Use a three-letter ISO 4217 currency code.");
        var name = ValidName(request.Name);
        var thresholds = ValidThresholds(request.Thresholds);
        var amount = ValidAmount(request.Amount);
        var period = ValidPeriod(request.Period);
        var accounts = await ValidAccountsAsync(request.OrganizationId, request.ProviderAccountIds, ct);
        var workspaces = await ValidWorkspacesAsync(request.OrganizationId, request.WorkspaceIds, ct);
        var vms = await ValidVmsAsync(request.OrganizationId, request.VmIds, ct);
        if (await db.Budgets.CountAsync(x => x.OrganizationId == request.OrganizationId, ct) >= MaxBudgetsPerOrganization)
            throw new BudgetException("budget_limit_reached", $"An organization can have at most {MaxBudgetsPerOrganization} budgets.", 409);
        await EnsureNameFreeAsync(request.OrganizationId, name, null, ct);
        var budget = new Budget
        {
            Id = ids.NewId("budget"), OrganizationId = request.OrganizationId, Name = name, Currency = currency,
            Period = period, Amount = amount, ThresholdsJson = JsonSerializer.Serialize(thresholds),
            ProviderAccountIdsJson = JsonSerializer.Serialize(accounts), WorkspaceIdsJson = JsonSerializer.Serialize(workspaces),
            VmIdsJson = JsonSerializer.Serialize(vms), CreatedBy = actor, CreatedAt = Now, UpdatedAt = Now
        };
        var change = await ApplyNotificationsAsync(budget, request.Notifications, ct);
        db.Budgets.Add(budget);
        await SaveOrForgetAsync(budget, change, ct);
        return change;
    }

    public async Task<BudgetChange> UpdateAsync(Budget budget, UpdateBudgetRequest request, CancellationToken ct)
    {
        if (budget.Revision != request.Revision)
            throw new BudgetException("revision_conflict", "This budget changed. Reload it before saving.", 409);
        var name = ValidName(request.Name);
        var thresholds = ValidThresholds(request.Thresholds);
        var amount = ValidAmount(request.Amount);
        var period = request.Period is null ? budget.Period : ValidPeriod(request.Period);
        var accounts = await ValidAccountsAsync(budget.OrganizationId, request.ProviderAccountIds, ct);
        var workspacesJson = request.WorkspaceIds is null ? budget.WorkspaceIdsJson
            : JsonSerializer.Serialize(await ValidWorkspacesAsync(budget.OrganizationId, request.WorkspaceIds, ct));
        var vmsJson = request.VmIds is null ? budget.VmIdsJson
            : JsonSerializer.Serialize(await ValidVmsAsync(budget.OrganizationId, request.VmIds, ct));
        await EnsureNameFreeAsync(budget.OrganizationId, name, budget.Id, ct);
        var thresholdsJson = JsonSerializer.Serialize(thresholds);
        var accountsJson = JsonSerializer.Serialize(accounts);
        var baselineChanged = budget.Amount != amount || budget.Period != period || budget.ThresholdsJson != thresholdsJson
            || budget.ProviderAccountIdsJson != accountsJson || budget.WorkspaceIdsJson != workspacesJson || budget.VmIdsJson != vmsJson;
        var change = await ApplyNotificationsAsync(budget, request.Notifications, ct);
        budget.Name = name;
        budget.Period = period;
        budget.Amount = amount;
        budget.ThresholdsJson = thresholdsJson;
        budget.ProviderAccountIdsJson = accountsJson;
        budget.WorkspaceIdsJson = workspacesJson;
        budget.VmIdsJson = vmsJson;
        if (baselineChanged)
        {
            // A changed amount, period, threshold or scope is a new alert baseline for the current period.
            budget.AlertPeriod = null;
            budget.AlertThresholdPercent = null;
            budget.AlertedAt = null;
        }
        budget.UpdatedAt = Now;
        budget.Revision++;
        await SaveOrForgetAsync(budget, change, ct);
        return change;
    }

    public async Task<StoredSecretReference?> DeleteAsync(Budget budget, CancellationToken ct)
    {
        var retired = WebhookSecret(budget);
        db.Budgets.Remove(budget);
        await db.SaveChangesAsync(ct);
        return retired;
    }

    // Called after the owning transaction commits; a leftover secret is harmless because nothing references it.
    public async Task ForgetSecretAsync(StoredSecretReference? reference, CancellationToken ct)
    {
        if (reference is null) return;
        try { await secrets.DeleteAsync(reference, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public async Task<BudgetResponse> ProjectAsync(Budget budget, string? webhookSigningSecret, CancellationToken ct)
    {
        var projection = Project(budget, await LoadInputsAsync(budget.OrganizationId, ct));
        var deliveries = await LoadDeliveriesAsync([budget.Id], ct);
        return Respond(budget, projection, deliveries, EmailAvailable) with { WebhookSigningSecret = webhookSigningSecret };
    }

    public Task<List<string>> OrganizationsWithBudgetsAsync(CancellationToken ct) =>
        db.Budgets.AsNoTracking().Select(x => x.OrganizationId).Distinct().ToListAsync(ct);

    /// <summary>
    /// Background evaluation: claims each newly crossed threshold once per budget baseline and budget period (calendar month,
    /// ISO week or calendar quarter, identified by the period key), then records the
    /// in-app alert and queues enabled channels. Returns the number of alerts claimed by this call.
    /// </summary>
    public async Task<int> EvaluateAlertsAsync(string organizationId, CancellationToken ct)
    {
        var budgets = await db.Budgets.AsNoTracking().Where(x => x.OrganizationId == organizationId).ToListAsync(ct);
        if (budgets.Count == 0) return 0;
        var inputs = await LoadInputsAsync(organizationId, ct);
        var claimed = 0;
        foreach (var budget in budgets)
        {
            var projection = Project(budget, inputs);
            if (projection.CrossedThreshold is not { } threshold) continue;
            var previous = budget.AlertPeriod == projection.Period ? budget.AlertThresholdPercent : null;
            if (previous >= threshold) continue;
            var crossed = Thresholds(budget).Where(x => x <= threshold && (previous is null || x > previous)).ToList();
            if (await ClaimAlertAsync(budget, projection, threshold, crossed, ct)) claimed++;
        }
        return claimed;
    }

    // The conditional update is the dedupe point: concurrent evaluators race on it and at most one wins per threshold.
    private async Task<bool> ClaimAlertAsync(Budget budget, BudgetProjectionResponse projection, int threshold,
        IReadOnlyList<int> crossed, CancellationToken ct)
    {
        var period = projection.Period;
        var now = Now;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var claimed = await db.Budgets.Where(x => x.Id == budget.Id && x.Revision == budget.Revision
                    && (x.AlertPeriod == null || x.AlertPeriod != period || x.AlertThresholdPercent == null || x.AlertThresholdPercent < threshold))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.AlertPeriod, period)
                    .SetProperty(x => x.AlertThresholdPercent, threshold).SetProperty(x => x.AlertedAt, now), ct);
            if (claimed != 1) return false;
            var payload = Payload(budget, projection, threshold, crossed, now);
            if (budget.InAppAlertsEnabled)
            {
                await audit.AppendAsync(budget.OrganizationId, AlertActor, ThresholdCrossedEvent, payload, ct);
                db.BudgetNotifications.Add(Notification(budget, BudgetNotification.InApp, period, threshold, payload, now,
                    BudgetNotification.Delivered));
            }
            if (budget.WebhookEnabled && budget.WebhookUrl is not null && budget.WebhookSecretPath is not null)
                db.BudgetNotifications.Add(Notification(budget, BudgetNotification.Webhook, period, threshold, payload, now,
                    BudgetNotification.Pending));
            // One email row per recipient; with email disabled or unconfigured nothing is queued or attempted.
            if (budget.EmailEnabled && EmailAvailable)
                foreach (var recipient in await EmailRecipientsAsync(budget, ct))
                    db.BudgetNotifications.Add(Notification(budget, BudgetNotification.Email, period, threshold, payload, now,
                        BudgetNotification.Pending, recipient));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another evaluator already recorded this alert; the next pass re-evaluates from committed state.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Current members in the budget's email audience, as user IDs; the delivery re-checks membership.</summary>
    public async Task<List<string>> EmailRecipientsAsync(Budget budget, CancellationToken ct)
    {
        var members = db.OrganizationMembers.AsNoTracking().Where(x => x.OrganizationId == budget.OrganizationId);
        if (budget.EmailAudience != Budget.EmailAudienceMembers)
            members = members.Where(x => x.Role == OrganizationRole.Owner || x.Role == OrganizationRole.Admin);
        return (await members.Select(x => x.UserId).Distinct().ToListAsync(ct)).Order(StringComparer.Ordinal)
            .Take(email.Value.RecipientLimit).ToList();
    }

    private BudgetNotification Notification(Budget budget, string channel, string period, int threshold, string payload,
        DateTimeOffset now, string status, string recipient = "") => new()
    {
        Id = ids.NewId("budget_notification"), OrganizationId = budget.OrganizationId, BudgetId = budget.Id,
        BudgetRevision = budget.Revision, Channel = channel, Recipient = recipient, Period = period, Threshold = threshold, PayloadJson = payload,
        Status = status, Attempts = status == BudgetNotification.Delivered ? 1 : 0,
        NextAttemptAt = status == BudgetNotification.Pending ? now : null,
        DeliveredAt = status == BudgetNotification.Delivered ? now : null, CreatedAt = now, UpdatedAt = now
    };

    // Secret-free payload: no webhook URL, signing secret or credentials; contributors and assumptions are included.
    private static string Payload(Budget budget, BudgetProjectionResponse projection, int threshold, IReadOnlyList<int> crossed,
        DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        type = ThresholdCrossedEvent, occurredAt = now, organizationId = budget.OrganizationId, budgetId = budget.Id,
        budget = budget.Name, period = projection.Period, periodType = projection.PeriodType, periodStart = projection.PeriodStart,
        periodEnd = projection.PeriodEnd, threshold, crossedThresholds = crossed, estimate = true,
        complete = projection.Complete, currency = budget.Currency, amount = budget.Amount,
        projectedAmount = projection.ProjectedAmount, projectedHourly = projection.ProjectedHourly,
        projectedMonthly = projection.ProjectedMonthly,
        percentOfBudget = projection.PercentOfBudget, overBudget = projection.OverBudget,
        contributors = projection.Contributors.Take(PayloadContributors).Select(x => new
        {
            workspaceId = x.WorkspaceId, workspace = x.WorkspaceName, providerAccountId = x.ProviderAccountId, region = x.Region,
            reservedVmCount = x.ReservedVmCount, projectedAmount = x.ProjectedAmount, projectedMonthly = x.ProjectedMonthly,
            pricingStatus = x.PricingStatus
        }),
        warnings = projection.Warnings, assumptions = projection.Assumptions
    }, PayloadJson);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private async Task<BudgetChange> ApplyNotificationsAsync(Budget budget, BudgetNotificationSettingsRequest? request,
        CancellationToken ct)
    {
        if (request is null) return new(budget, null, null);
        if (request.InApp is { } inApp) budget.InAppAlertsEnabled = inApp;
        if (request.EmailAudience is { } audience)
            budget.EmailAudience = audience.Trim().ToLowerInvariant() is Budget.EmailAudienceAdmins or Budget.EmailAudienceMembers
                ? audience.Trim().ToLowerInvariant()
                : throw new BudgetException("budget_email_audience_invalid", "Choose email recipients: admins or members.");
        if (request.Email is { } emailEnabled) budget.EmailEnabled = emailEnabled;
        if (request.WebhookUrl is { } url)
        {
            budget.WebhookUrl = url.Trim().Length == 0 ? null : ValidWebhookUrl(url);
            if (budget.WebhookUrl is null) budget.WebhookEnabled = false;
        }
        if (request.Webhook is { } webhook) budget.WebhookEnabled = webhook;
        if (budget.WebhookEnabled && budget.WebhookUrl is null)
            throw new BudgetException("budget_webhook_url_required", "Enter an HTTPS webhook URL to enable webhook notifications.");
        var retired = WebhookSecret(budget);
        var needsSecret = budget.WebhookUrl is not null && (retired is null || request.RotateWebhookSecret);
        if (budget.WebhookUrl is null && retired is not null)
        {
            SetWebhookSecret(budget, null);
            return new(budget, null, retired);
        }
        if (!needsSecret) return new(budget, null, null);
        var secret = "mcwh_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var reference = await secrets.PutAsync(new SecretStoreWriteRequest(budget.OrganizationId, secret)
            { IntegrationId = "budget-webhook-" + budget.Id, CredentialGeneration = Guid.NewGuid().ToString("N") }, ct);
        SetWebhookSecret(budget, reference);
        return new(budget, secret, retired);
    }

    private async Task SaveOrForgetAsync(Budget budget, BudgetChange change, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch when (change.WebhookSigningSecret is not null)
        {
            await ForgetSecretAsync(WebhookSecret(budget), CancellationToken.None);
            throw;
        }
    }

    public static StoredSecretReference? WebhookSecret(Budget budget) =>
        budget.WebhookSecretPath is null ? null
            : new(budget.WebhookSecretProvider ?? OpenBaoSecretStore.ProviderName, budget.WebhookSecretPath, budget.WebhookSecretVersion);

    private static void SetWebhookSecret(Budget budget, StoredSecretReference? reference)
    {
        budget.WebhookSecretProvider = reference?.Provider;
        budget.WebhookSecretPath = reference?.Path;
        budget.WebhookSecretVersion = reference?.Version;
    }

    public static string ValidWebhookUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 2048 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.IsLoopback
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && !IsPublicAddress(IPAddress.Parse(uri.DnsSafeHost))
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new BudgetException("budget_webhook_url_invalid",
                "Use a public HTTPS webhook URL of at most 2048 characters without credentials or a fragment.");
        return uri.AbsoluteUri;
    }

    // Webhook targets must be public: no loopback, private, link-local, carrier-grade NAT, documentation, multicast or unspecified ranges.
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 192 && b[1] == 0 && b[2] == 0
                || b[0] == 198 && b[1] is 18 or 19 || b[0] == 192 && b[1] == 0 && b[2] == 2
                || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc
            || b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

    private static BudgetResponse Respond(Budget budget, BudgetProjectionResponse projection,
        ILookup<string, BudgetNotification> deliveries, bool emailAvailable)
    {
        var target = budget.WebhookUrl is null ? null : new Uri(budget.WebhookUrl).GetLeftPart(UriPartial.Authority);
        var lastAlert = budget.AlertPeriod is null || budget.AlertThresholdPercent is null || budget.AlertedAt is null ? null
            : new BudgetLastAlertResponse(budget.AlertPeriod, budget.AlertThresholdPercent.Value, budget.AlertedAt.Value);
        return new(budget.Id, budget.OrganizationId, budget.Name, budget.Currency, budget.Amount, Thresholds(budget),
            AccountIds(budget), budget.Revision, budget.CreatedAt, budget.UpdatedAt, projection,
            new(budget.InAppAlertsEnabled, budget.WebhookEnabled, target, budget.WebhookSecretPath is not null,
                budget.EmailEnabled, budget.EmailAudience, emailAvailable), lastAlert,
            deliveries[budget.Id].OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Channel, StringComparer.Ordinal).Take(10)
                .Select(x => new BudgetNotificationDeliveryResponse(x.Id, x.Channel, x.Period, x.Threshold, x.Status, x.Attempts,
                    x.LastError, x.CreatedAt, x.DeliveredAt, x.Status == BudgetNotification.Pending ? x.NextAttemptAt : null))
                .ToList(), budget.Period, Ids(budget.WorkspaceIdsJson), Ids(budget.VmIdsJson));
    }

    // Recent deliveries only (current and previous period of each type); ordering happens in memory for SQLite.
    private async Task<ILookup<string, BudgetNotification>> LoadDeliveriesAsync(IReadOnlyList<string> budgetIds,
        CancellationToken ct)
    {
        var periods = BudgetPeriods.RecentKeys(Now);
        var rows = await db.BudgetNotifications.AsNoTracking()
            .Where(x => budgetIds.Contains(x.BudgetId) && periods.Contains(x.Period)).ToListAsync(ct);
        return rows.ToLookup(x => x.BudgetId, StringComparer.Ordinal);
    }

    private BudgetProjectionResponse Project(Budget budget, Inputs inputs) =>
        BudgetProjector.Project(budget, Thresholds(budget),
            new BudgetScope(AccountIds(budget), Ids(budget.WorkspaceIdsJson), Ids(budget.VmIdsJson)), inputs.Workspaces,
            inputs.Accounts, Policy, Now);

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

    private async Task<List<string>> ValidWorkspacesAsync(string organizationId, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        var selected = SelectedIds(requested, "workspaces");
        if (selected.Count == 0) return selected;
        var known = await db.DevelopmentWorkspaces.Where(x => x.OrganizationId == organizationId && selected.Contains(x.Id)
            && x.VolumeStatus != "deleted").CountAsync(ct);
        if (known != selected.Count)
            throw new BudgetException("budget_scope_invalid", "Every selected workspace must exist in this organization.");
        return selected;
    }

    private async Task<List<string>> ValidVmsAsync(string organizationId, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        var selected = SelectedIds(requested, "VMs");
        if (selected.Count == 0) return selected;
        var known = await db.DevelopmentWorkspaceVms.Where(x => x.OrganizationId == organizationId && selected.Contains(x.Id)).CountAsync(ct);
        if (known != selected.Count)
            throw new BudgetException("budget_scope_invalid", "Every selected VM must exist in this organization.");
        return selected;
    }

    private static List<string> SelectedIds(IReadOnlyList<string>? requested, string what)
    {
        var selected = (requested ?? []).Select(x => x?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count > MaxScopedResources || selected.Any(x => x.Length is 0 or > 128))
            throw new BudgetException("budget_scope_invalid", $"Select up to {MaxScopedResources} {what}, or none for all.");
        return selected.Order(StringComparer.Ordinal).ToList();
    }

    private static string ValidPeriod(string? period)
    {
        var value = (period ?? BudgetPeriods.Month).Trim().ToLowerInvariant();
        return BudgetPeriods.IsValid(value) ? value
            : throw new BudgetException("budget_period_invalid", "Choose a budget period: month, week or quarter.");
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
            throw new BudgetException("budget_amount_invalid", "Use a positive amount with at most two decimal places.");
        return amount;
    }

    private static List<int> Thresholds(Budget budget) => JsonSerializer.Deserialize<List<int>>(budget.ThresholdsJson) ?? [];
    private static List<string> AccountIds(Budget budget) => Ids(budget.ProviderAccountIdsJson);
    private static List<string> Ids(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private sealed record Inputs(IReadOnlyList<DevelopmentWorkspace> Workspaces, IReadOnlyDictionary<string, ProviderAccount> Accounts);
}
