namespace Workspaces.Common.Models;

// Budgets are informational only: no create, start, stop or resize path may read them.
public sealed class Budget
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Currency { get; set; } = default!;
    public decimal MonthlyAmount { get; set; }
    public string ThresholdsJson { get; set; } = "[]";
    public string ProviderAccountIdsJson { get; set; } = "[]";
    // Highest threshold already alerted in AlertPeriod (UTC "yyyy-MM"); dedupes alerts per threshold per month.
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
    public string CreatedBy { get; set; } = default!;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
