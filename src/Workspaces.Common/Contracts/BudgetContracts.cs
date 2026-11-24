namespace Workspaces.Common.Contracts;

public sealed record CreateBudgetRequest(string OrganizationId, string Name, decimal MonthlyAmount, string? Currency = null,
    IReadOnlyList<int>? Thresholds = null, IReadOnlyList<string>? ProviderAccountIds = null,
    BudgetNotificationSettingsRequest? Notifications = null);

public sealed record UpdateBudgetRequest(long Revision, string Name, decimal MonthlyAmount,
    IReadOnlyList<int>? Thresholds = null, IReadOnlyList<string>? ProviderAccountIds = null,
    BudgetNotificationSettingsRequest? Notifications = null);

// Null fields keep the current value. WebhookUrl "" removes the webhook. The full URL is write-only.
// EmailAudience is "admins" (owners and admins) or "members" (every current member).
public sealed record BudgetNotificationSettingsRequest(bool? InApp = null, bool? Webhook = null, string? WebhookUrl = null,
    bool RotateWebhookSecret = false, bool? Email = null, string? EmailAudience = null);

public sealed record BudgetResponse(string Id, string OrganizationId, string Name, string Currency, decimal MonthlyAmount,
    IReadOnlyList<int> Thresholds, IReadOnlyList<string> ProviderAccountIds, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, BudgetProjectionResponse Projection, BudgetNotificationSettingsResponse Notifications,
    BudgetLastAlertResponse? LastAlert, IReadOnlyList<BudgetNotificationDeliveryResponse> Deliveries)
{
    // Returned once, only by the create or update call that generated or rotated the secret.
    public string? WebhookSigningSecret { get; init; }

    // What providers report they have charged, kept apart from the projection and never converted between currencies.
    public BudgetProviderChargesResponse? ProviderCharges { get; init; }
}

// WebhookTarget is only the scheme, host and port so query-string tokens in the URL are never echoed back.
// EmailAvailable is false until the operator configures outbound email; until then no email is queued or sent.
public sealed record BudgetNotificationSettingsResponse(bool InApp, bool Webhook, string? WebhookTarget, bool WebhookSecretConfigured,
    bool Email = false, string EmailAudience = "admins", bool EmailAvailable = false);

public sealed record BudgetLastAlertResponse(string Period, int Threshold, DateTimeOffset AlertedAt);

public sealed record BudgetNotificationDeliveryResponse(string Id, string Channel, string Period, int Threshold, string Status,
    int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt);

// Every amount is an estimate derived from recorded rates. Provider-reported charges are never mixed in; see
// BudgetResponse.ProviderCharges, so ProviderChargesAvailable stays false here.
public sealed record BudgetProjectionResponse(string Period, bool Estimate, bool Complete, decimal ProjectedHourly,
    decimal ProjectedMonthly, decimal PercentOfBudget, int? CrossedThreshold, bool OverBudget, bool ProviderChargesAvailable,
    IReadOnlyList<BudgetProviderProjectionResponse> Providers, IReadOnlyList<BudgetContributorResponse> Contributors,
    IReadOnlyList<BudgetExcludedWorkspaceResponse> Excluded, IReadOnlyList<string> Warnings, IReadOnlyList<string> Assumptions);

public sealed record BudgetProviderProjectionResponse(string ProviderAccountId, string ProviderName, string ProviderType,
    decimal ComputeMonthly, decimal StorageMonthly, decimal ProjectedMonthly, int WorkspaceCount, int ReservedVmCount,
    bool Complete, IReadOnlyList<string> Assumptions);

public sealed record BudgetContributorResponse(string WorkspaceId, string WorkspaceName, string ProviderAccountId, string Region,
    IReadOnlyList<string> Skus, int ReservedVmCount, decimal ComputeHourly, decimal ComputeMonthly, decimal StorageMonthly,
    decimal ProjectedMonthly, string PricingStatus, DateTimeOffset? PricingUpdatedAt, IReadOnlyList<BudgetPriceLineResponse> Lines);

// Source is "catalog" or "fallback" (the rate recorded when the VM was selected). Status is "current", "stale" or
// "missing"; a missing price has a null Amount and is never counted as zero.
public sealed record BudgetPriceLineResponse(string Resource, string ResourceId, string? Sku, decimal Quantity, string Source,
    string? CatalogEntryId, string BillingModel, string Currency, decimal? Amount, string Status, DateTimeOffset? PricedAt);

public sealed record BudgetExcludedWorkspaceResponse(string WorkspaceId, string WorkspaceName, string Currency, string Reason);

// Status is "available", "stale" (older than the freshness window), "failed" (the last refresh failed; Amount is the last
// reported value, if any), "unsupported" (the provider has no billing API) or "not_retrieved". Amount is null, never zero,
// when nothing was reported. Totals are per currency; different currencies are never summed.
public sealed record BudgetProviderChargesResponse(string Period, bool Complete,
    IReadOnlyList<BudgetProviderChargeResponse> Accounts, IReadOnlyList<BudgetProviderChargeTotalResponse> Totals,
    IReadOnlyList<string> Notes);

public sealed record BudgetProviderChargeResponse(string ProviderAccountId, string ProviderName, string ProviderType, string Status,
    decimal? Amount, string? Currency, DateTimeOffset? AsOf, DateTimeOffset? AttemptedAt, string? Source, string Message);

public sealed record BudgetProviderChargeTotalResponse(string Currency, decimal Amount, int AccountCount);
