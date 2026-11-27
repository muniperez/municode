namespace Municode.Common.Models;

// Budgets are informational only: no create, start, stop or resize path may read them.
public sealed class Budget
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Currency { get; set; } = default!;
    // "month" (calendar month), "week" (ISO week, Monday to Sunday) or "quarter" (calendar quarter), all in UTC.
    public string Period { get; set; } = "month";
    // Budget amount for one period.
    public decimal Amount { get; set; }
    public string ThresholdsJson { get; set; } = "[]";
    // Scope: provider accounts narrow the budget; selected workspaces (all their VMs and the volume) and individual VMs
    // (compute only) narrow it further. Empty lists select everything.
    public string ProviderAccountIdsJson { get; set; } = "[]";
    public string WorkspaceIdsJson { get; set; } = "[]";
    public string VmIdsJson { get; set; } = "[]";
    // Highest threshold already alerted in AlertPeriod (the period key, for example "2026-11", "2026-W48" or "2026-Q4");
    // dedupes alerts per threshold per budget period.
    public string? AlertPeriod { get; set; }
    public int? AlertThresholdPercent { get; set; }
    public DateTimeOffset? AlertedAt { get; set; }
    // Notification channels. Delivery is informational and never changes resources or budget state.
    public bool InAppAlertsEnabled { get; set; } = true;
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    // Reference to the HMAC signing secret in the secret store; the secret itself is never stored here.
    public string? WebhookSecretProvider { get; set; }
    public string? WebhookSecretPath { get; set; }
    public string? WebhookSecretVersion { get; set; }
    // Email goes to current organization members only, chosen by role; addresses are resolved at delivery time.
    public bool EmailEnabled { get; set; }
    public string EmailAudience { get; set; } = EmailAudienceAdmins;
    public const string EmailAudienceAdmins = "admins";
    public const string EmailAudienceMembers = "members";
    public string CreatedBy { get; set; } = default!;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
