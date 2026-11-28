namespace Municode.Common.Models;

// Outbox row for one threshold alert on one channel and recipient; unique per budget revision, period, threshold,
// channel and recipient.
public sealed class BudgetNotification
{
    public const string InApp = "in_app";
    public const string Webhook = "webhook";
    public const string Email = "email";
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string BudgetId { get; set; } = default!;
    public long BudgetRevision { get; set; }
    public string Channel { get; set; } = default!;
    // Member user ID for email rows; empty for channels without per-recipient delivery.
    public string Recipient { get; set; } = "";
    public string Period { get; set; } = default!;
    public int Threshold { get; set; }
    // Secret-free event body; webhooks sign and send exactly these bytes.
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
