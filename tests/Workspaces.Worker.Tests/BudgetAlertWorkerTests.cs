using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workspaces.Common.Contracts;
using Workspaces.Common.Models;
using Workspaces.Common.Services;
using Workspaces.Worker.Budgets;

namespace Workspaces.Worker.Tests.DevelopmentWorkspaces;

// Background budget evaluation and notification delivery (#110).
public sealed partial class DevelopmentWorkspaceProcessorTests
{
    private const string BudgetHook = "https://hooks.example.test/budget?token=sensitive-token";

    [Fact]
    public async Task BudgetWorkerSendsOneSignedWebhookPerThresholdPerPeriodAndNeverTouchesResources()
    {
        var (budgets, worker, sender) = BudgetWorker();
        var workspace = await StartedBudgetWorkspace();
        var created = await budgets.CreateAsync(new("org", "Team", 100m, null, [50, 80, 100],
            Notifications: new(Webhook: true, WebhookUrl: BudgetHook)), "actor", default);
        var secret = created.WebhookSigningSecret!;

        await worker.RunOnceAsync(default);
        var request = Assert.Single(sender.Requests);
        Assert.Equal(BudgetHook, request.Url.AbsoluteUri);
        var timestamp = long.Parse(request.Headers[BudgetNotificationDispatcher.TimestampHeader]);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), timestamp);
        Assert.Equal(BudgetNotificationDispatcher.Sign(secret, timestamp, request.Body),
            request.Headers[BudgetNotificationDispatcher.SignatureHeader]);
        Assert.Equal(BudgetService.ThresholdCrossedEvent, request.Headers[BudgetNotificationDispatcher.EventHeader]);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal((50, true, 74m), (body.GetProperty("threshold").GetInt32(), body.GetProperty("estimate").GetBoolean(),
            body.GetProperty("projectedMonthly").GetDecimal()));
        Assert.Equal(workspace.Id, body.GetProperty("contributors")[0].GetProperty("workspaceId").GetString());
        Assert.Contains(body.GetProperty("assumptions").EnumerateArray(), x => x.GetString()!.Contains("never blocks"));
        Assert.DoesNotContain("sensitive-token", request.Body);
        Assert.DoesNotContain(secret, request.Body);
        Assert.DoesNotContain("hooks.example.test", request.Body);

        // Evaluation runs on its interval; repeated cycles in the same period do not re-send.
        clock.Advance(TimeSpan.FromMinutes(10));
        await worker.RunOnceAsync(default);
        clock.Advance(TimeSpan.FromMinutes(10));
        await worker.RunOnceAsync(default);
        Assert.Single(sender.Requests);
        var notifications = await db.BudgetNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(["in_app:delivered", "webhook:delivered"], notifications.Select(x => $"{x.Channel}:{x.Status}").Order());
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(x => x.Action == BudgetService.ThresholdCrossedEvent).ToListAsync());

        clock.Advance(TimeSpan.FromDays(32));
        await worker.RunOnceAsync(default);
        Assert.Equal(2, sender.Requests.Count);
        Assert.NotEqual(body.GetProperty("period").GetString(),
            JsonDocument.Parse(sender.Requests[1].Body).RootElement.GetProperty("period").GetString());

        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.WorkspaceId == workspace.Id);
        Assert.True(vm.ComputeReserved);
        Assert.Equal("running", vm.DesiredState);
    }

    [Fact]
    public async Task DisabledBudgetChannelsStopDelivery()
    {
        var (budgets, worker, sender) = BudgetWorker();
        await StartedBudgetWorkspace();
        var quiet = await budgets.CreateAsync(new("org", "Quiet", 100m, null, [50],
            Notifications: new(InApp: false, Webhook: true, WebhookUrl: BudgetHook)), "actor", default);
        Assert.Equal(1, await budgets.EvaluateAlertsAsync("org", default));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.Action == BudgetService.ThresholdCrossedEvent));

        var budget = await db.Budgets.SingleAsync(x => x.Id == quiet.Budget.Id);
        await budgets.UpdateAsync(budget, new(budget.Revision, "Quiet", 100m, [50], [], new(Webhook: false)), default);
        await worker.RunOnceAsync(default);

        Assert.Empty(sender.Requests);
        var cancelled = await db.BudgetNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(("webhook", BudgetNotification.Cancelled), (cancelled.Channel, cancelled.Status));
    }

    [Fact]
    public async Task FailedBudgetWebhooksRetryWithBackoffWithoutChangingBudgetState()
    {
        var (budgets, worker, sender) = BudgetWorker(new() { MaxDeliveryAttempts = 3, RetryBaseSeconds = 60, RetryMaxSeconds = 90 });
        sender.Result = new(false, "HTTP 503");
        await StartedBudgetWorkspace();
        var created = await budgets.CreateAsync(new("org", "Flaky", 100m, null, [50],
            Notifications: new(Webhook: true, WebhookUrl: BudgetHook)), "actor", default);
        await worker.RunOnceAsync(default);
        var before = await db.Budgets.AsNoTracking().SingleAsync(x => x.Id == created.Budget.Id);

        var row = await Webhook();
        Assert.Equal((1, BudgetNotification.Pending, "HTTP 503"), (row.Attempts, row.Status, row.LastError));
        Assert.Equal(clock.GetUtcNow().AddSeconds(60), row.NextAttemptAt);
        clock.Advance(TimeSpan.FromSeconds(59));
        await worker.RunOnceAsync(default);
        Assert.Single(sender.Requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        await worker.RunOnceAsync(default);
        row = await Webhook();
        Assert.Equal((2, BudgetNotification.Pending), (row.Attempts, row.Status));
        Assert.Equal(clock.GetUtcNow().AddSeconds(90), row.NextAttemptAt);

        clock.Advance(TimeSpan.FromSeconds(90));
        await worker.RunOnceAsync(default);
        row = await Webhook();
        Assert.Equal(3, sender.Requests.Count);
        Assert.Equal((3, BudgetNotification.Failed, null), (row.Attempts, row.Status, row.NextAttemptAt));
        Assert.StartsWith("HTTP 503", row.LastError);
        Assert.Equal(sender.Requests[0].Body, sender.Requests[2].Body);
        Assert.Equal(sender.Requests[0].Headers[BudgetNotificationDispatcher.DeliveryHeader],
            sender.Requests[2].Headers[BudgetNotificationDispatcher.DeliveryHeader]);

        var after = await db.Budgets.AsNoTracking().SingleAsync(x => x.Id == created.Budget.Id);
        Assert.Equal((before.Revision, before.AlertPeriod, before.AlertThresholdPercent, before.WebhookEnabled),
            (after.Revision, after.AlertPeriod, after.AlertThresholdPercent, after.WebhookEnabled));
        Assert.True((await db.BudgetNotifications.AsNoTracking().SingleAsync(x => x.Channel == "in_app")).Status == "delivered");

        async Task<BudgetNotification> Webhook() =>
            await db.BudgetNotifications.AsNoTracking().SingleAsync(x => x.Channel == BudgetNotification.Webhook);
    }

    [Theory]
    [InlineData("203.0.113.10", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void BudgetWebhooksOnlyTargetPublicAddresses(string address, bool allowed) =>
        Assert.Equal(allowed, BudgetService.IsPublicAddress(System.Net.IPAddress.Parse(address)));

    private async Task<DevelopmentWorkspace> StartedBudgetWorkspace()
    {
        var workspace = await lifecycle.CreateAsync(new("org", "Budget fixture", "image", "standard", "plan", 20, "budget"), "actor", default);
        await lifecycle.StartAsync(workspace.Id, "org", default);
        return workspace;
    }

    private (BudgetService Budgets, BudgetAlertWorker Worker, SyntheticWebhookSender Sender) BudgetWorker(
        BudgetAlertWorkerOptions? settings = null)
    {
        settings ??= new();
        var sender = new SyntheticWebhookSender();
        var provider = new ServiceCollection()
            .AddSingleton(db).AddSingleton<TimeProvider>(clock).AddSingleton<IIdGenerator, IdGenerator>()
            .AddSingleton<ISecretStore>(secrets).AddSingleton<IBudgetWebhookSender>(sender)
            .AddSingleton<IOptions<DevelopmentWorkspaceOptions>>(Options.Create(policy))
            .AddSingleton<IOptions<BudgetAlertWorkerOptions>>(Options.Create(settings))
            .AddScoped<AuditEventService>().AddScoped<BudgetService>().AddScoped<BudgetNotificationDispatcher>()
            .BuildServiceProvider();
        var worker = new BudgetAlertWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock, Options.Create(settings),
            NullLogger<BudgetAlertWorker>.Instance);
        return (ActivatorUtilities.CreateInstance<BudgetService>(provider), worker, sender);
    }

    private sealed class SyntheticWebhookSender : IBudgetWebhookSender
    {
        public List<BudgetWebhookRequest> Requests { get; } = [];
        public BudgetWebhookResult Result { get; set; } = new(true, null);

        public Task<BudgetWebhookResult> SendAsync(BudgetWebhookRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
