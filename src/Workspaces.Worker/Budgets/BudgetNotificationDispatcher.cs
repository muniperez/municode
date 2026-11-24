using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Services;

namespace Workspaces.Worker.Budgets;

/// <summary>
/// Delivers queued budget webhooks with HMAC signatures and exponential backoff. Only notification rows change here:
/// delivery outcomes never touch budgets, workspaces or VMs.
/// </summary>
public sealed class BudgetNotificationDispatcher(WorkspaceDbContext db, ISecretStore secrets, IBudgetWebhookSender sender,
    TimeProvider clock, IOptions<BudgetAlertWorkerOptions> options)
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
                .Where(x => x.Status == BudgetNotification.Pending && x.Channel == BudgetNotification.Webhook).ToListAsync(ct))
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
        var reference = budget is null ? null : BudgetService.WebhookSecret(budget);
        if (budget is null || !budget.WebhookEnabled || budget.WebhookUrl is null || reference is null)
        {
            await FinishAsync(notification.Id, attempt, BudgetNotification.Cancelled, "Webhook channel was disabled before delivery.", ct);
            return false;
        }

        string secret;
        try { secret = await secrets.GetAsync(reference, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RetryAsync(notification.Id, attempt, "Signing secret unavailable", ct);
            return false;
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
        BudgetWebhookResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(Settings.WebhookTimeout);
            try { result = await sender.SendAsync(new(new Uri(budget.WebhookUrl), notification.PayloadJson, headers), timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = new(false, "Timed out"); }
        }
        if (result.Delivered)
        {
            await FinishAsync(notification.Id, attempt, BudgetNotification.Delivered, null, ct);
            return true;
        }
        await RetryAsync(notification.Id, attempt, result.Error ?? "Delivery failed", ct);
        return false;
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
