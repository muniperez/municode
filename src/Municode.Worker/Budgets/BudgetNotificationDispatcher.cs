using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Municode.Common.Data;
using Municode.Common.Models;
using Municode.Common.Services;

namespace Municode.Worker.Budgets;

/// <summary>
/// Delivers queued budget webhooks (HMAC-signed) and emails with exponential backoff. Only notification rows change here:
/// delivery outcomes never touch budgets, workspaces or VMs.
/// </summary>
public sealed class BudgetNotificationDispatcher(WorkspaceDbContext db, ISecretStore secrets, IBudgetWebhookSender sender,
    IBudgetEmailSender emailSender, TimeProvider clock, IOptions<BudgetAlertWorkerOptions> options,
    IOptions<BudgetEmailOptions> email)
{
    public const string EventHeader = "X-Municode-Event";
    public const string DeliveryHeader = "X-Municode-Delivery";
    public const string TimestampHeader = "X-Municode-Timestamp";
    public const string SignatureHeader = "X-Municode-Signature";
    private BudgetAlertWorkerOptions Settings => options.Value;

    public static string Sign(string secret, long timestamp, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body))).ToLowerInvariant();

    public async Task<int> DeliverDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Due times are filtered in memory because SQLite cannot compare DateTimeOffset values.
        var due = (await db.BudgetNotifications.AsNoTracking()
                .Where(x => x.Status == BudgetNotification.Pending
                    && (x.Channel == BudgetNotification.Webhook || x.Channel == BudgetNotification.Email)).ToListAsync(ct))
            .Where(x => x.NextAttemptAt is null || x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt ?? x.CreatedAt).Take(Math.Clamp(Settings.DeliveryBatchSize, 1, 500)).ToList();
        var delivered = 0;
        foreach (var notification in due)
            if (await DeliverAsync(notification, ct)) delivered++;
        return delivered;
    }

    private async Task<bool> DeliverAsync(BudgetNotification notification, CancellationToken ct)
    {
        var attempt = notification.Attempts + 1;
        var now = clock.GetUtcNow();
        // Claiming the attempt number fences concurrent dispatchers; the lease makes a crashed attempt retryable.
        var lease = now + Settings.WebhookTimeout + TimeSpan.FromMinutes(1);
        var claimed = await db.BudgetNotifications
            .Where(x => x.Id == notification.Id && x.Status == BudgetNotification.Pending && x.Attempts == notification.Attempts)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Attempts, attempt).SetProperty(x => x.NextAttemptAt, lease)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (claimed != 1) return false;

        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == notification.BudgetId, ct);
        var outcome = notification.Channel == BudgetNotification.Email
            ? await SendEmailAsync(notification, budget, ct)
            : await SendWebhookAsync(notification, budget, ct);
        switch (outcome.Status)
        {
            case BudgetNotification.Delivered:
                await FinishAsync(notification.Id, attempt, BudgetNotification.Delivered, null, ct);
                return true;
            case BudgetNotification.Cancelled:
                await FinishAsync(notification.Id, attempt, BudgetNotification.Cancelled, outcome.Error, ct);
                return false;
            default:
                await RetryAsync(notification.Id, attempt, outcome.Error ?? "Delivery failed", ct);
                return false;
        }
    }

    private readonly record struct Outcome(string Status, string? Error)
    {
        public static Outcome Delivered => new(BudgetNotification.Delivered, null);
        public static Outcome Cancel(string reason) => new(BudgetNotification.Cancelled, reason);
        public static Outcome Retry(string? error) => new(BudgetNotification.Pending, error ?? "Delivery failed");
    }

    private async Task<Outcome> SendWebhookAsync(BudgetNotification notification, Budget? budget, CancellationToken ct)
    {
        var reference = budget is null ? null : BudgetService.WebhookSecret(budget);
        if (budget is null || !budget.WebhookEnabled || budget.WebhookUrl is null || reference is null)
            return Outcome.Cancel("Webhook channel was disabled before delivery.");

        string secret;
        try { secret = await secrets.GetAsync(reference, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Outcome.Retry("Signing secret unavailable");
        }

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventHeader] = BudgetService.ThresholdCrossedEvent,
            [DeliveryHeader] = notification.Id,
            [TimestampHeader] = timestamp.ToString(CultureInfo.InvariantCulture),
            [SignatureHeader] = Sign(secret, timestamp, notification.PayloadJson),
            ["User-Agent"] = "Municode-Budget-Webhook/1"
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Settings.WebhookTimeout);
        try
        {
            var result = await sender.SendAsync(new(new Uri(budget.WebhookUrl), notification.PayloadJson, headers), timeout.Token);
            return result.Delivered ? Outcome.Delivered : Outcome.Retry(result.Error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Outcome.Retry("Timed out"); }
    }

    // Recipients are re-checked at delivery: only current members in the budget's audience with a valid address get email.
    private async Task<Outcome> SendEmailAsync(BudgetNotification notification, Budget? budget, CancellationToken ct)
    {
        if (!email.Value.IsConfigured) return Outcome.Cancel("Email delivery is not configured.");
        if (budget is null || !budget.EmailEnabled) return Outcome.Cancel("Email channel was disabled before delivery.");
        var member = await db.OrganizationMembers.AsNoTracking()
            .Where(x => x.OrganizationId == budget.OrganizationId && x.UserId == notification.Recipient)
            .Select(x => new { x.Role, x.User.Email }).FirstOrDefaultAsync(ct);
        if (member is null || budget.EmailAudience != Budget.EmailAudienceMembers
                && member.Role is not (OrganizationRole.Owner or OrganizationRole.Admin))
            return Outcome.Cancel("Recipient is no longer in this budget's email audience.");
        if (!MailAddress.TryCreate(member.Email, out var address))
            return Outcome.Cancel("Recipient has no valid email address.");

        var message = BudgetEmailContent.Render(address.Address, notification.PayloadJson, budget.EmailAudience);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(email.Value.Timeout);
        try
        {
            var result = await emailSender.SendAsync(message, timeout.Token);
            return result.Delivered ? Outcome.Delivered : Outcome.Retry(result.Error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Outcome.Retry("Timed out"); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Exception messages can echo server responses or addresses; only the type is recorded.
            return Outcome.Retry($"Email transport unavailable ({error.GetType().Name})");
        }
    }

    private Task RetryAsync(string id, int attempt, string error, CancellationToken ct) => attempt >= Settings.MaxAttempts
        ? FinishAsync(id, attempt, BudgetNotification.Failed, $"{error}; gave up after {attempt} attempt(s)", ct)
        : UpdateAsync(id, attempt, BudgetNotification.Pending, error, clock.GetUtcNow() + Settings.RetryDelay(attempt), ct);

    private Task FinishAsync(string id, int attempt, string status, string? error, CancellationToken ct) =>
        UpdateAsync(id, attempt, status, error, null, ct);

    private async Task UpdateAsync(string id, int attempt, string status, string? error, DateTimeOffset? next, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? delivered = status == BudgetNotification.Delivered ? now : null;
        var message = error is null ? null : error.Length <= 256 ? error : error[..256];
        await db.BudgetNotifications.Where(x => x.Id == id && x.Attempts == attempt)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, status).SetProperty(x => x.LastError, message)
                .SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.DeliveredAt, delivered)
                .SetProperty(x => x.UpdatedAt, now), CancellationToken.None);
    }
}
