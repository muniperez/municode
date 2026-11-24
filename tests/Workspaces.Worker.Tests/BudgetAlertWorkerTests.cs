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

    [Fact]
    public async Task BudgetEmailGoesOncePerAudienceMemberPerThresholdAndContainsOnlyEstimates()
    {
        var (budgets, worker, sender) = BudgetWorker();
        await SeedBudgetMembers();
        await StartedBudgetWorkspace();
        var created = await budgets.CreateAsync(new("org", "Team", 100m, null, [50, 80],
            Notifications: new(Webhook: true, WebhookUrl: BudgetHook, Email: true)), "actor", default);

        // Operator email disabled: nothing is queued or attempted, and webhook delivery is unchanged.
        await worker.RunOnceAsync(default);
        Assert.Empty(emailSender.Messages);
        Assert.Single(sender.Requests);
        Assert.False(await db.BudgetNotifications.AnyAsync(x => x.Channel == BudgetNotification.Email));

        ConfigureBudgetEmail();
        // The claim uses ExecuteUpdate, so reload the budget as the API would instead of reusing the tracked instance.
        db.ChangeTracker.Clear();
        var budget = await db.Budgets.SingleAsync(x => x.Id == created.Budget.Id);
        await budgets.UpdateAsync(budget, new(budget.Revision, "Team", 120m, [50, 80], []), default);
        clock.Advance(TimeSpan.FromMinutes(10));
        await worker.RunOnceAsync(default);
        Assert.Equal(["admin@example.test", "owner@example.test"], emailSender.Messages.Select(x => x.To).Order());
        var message = emailSender.Messages[0];
        Assert.Contains("estimate reached 50%", message.Subject);
        Assert.Contains("Projected this month: 74 USD", message.TextBody);
        Assert.Contains("Budget fixture", message.TextBody);
        Assert.Contains(BudgetEmailContent.EstimateNotice, message.TextBody);
        Assert.Contains(BudgetEmailContent.InformOnlyNotice, message.TextBody);
        foreach (var secret in new[] { "sensitive-token", "hooks.example.test", created.WebhookSigningSecret!, "smtp-password" })
            Assert.DoesNotContain(secret, message.Subject + message.TextBody);

        // Repeated evaluations in the same period never re-send; delivered rows stay delivered.
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            await worker.RunOnceAsync(default);
        }
        Assert.Equal(2, emailSender.Messages.Count);
        Assert.All(await db.BudgetNotifications.AsNoTracking().Where(x => x.Channel == BudgetNotification.Email).ToListAsync(),
            x => Assert.Equal(BudgetNotification.Delivered, x.Status));
    }

    [Fact]
    public async Task BudgetEmailRetriesTransportFailuresAndStopsWhenChannelOrRecipientIsRemoved()
    {
        var (budgets, worker, _) = BudgetWorker(new() { MaxDeliveryAttempts = 2, RetryBaseSeconds = 60 });
        await SeedBudgetMembers();
        ConfigureBudgetEmail();
        emailSender.Result = new(false, "SMTP error (451)");
        await StartedBudgetWorkspace();
        var created = await budgets.CreateAsync(new("org", "Mail", 100m, null, [50],
            Notifications: new(Email: true, EmailAudience: "members")), "actor", default);
        await worker.RunOnceAsync(default);
        Assert.Equal(3, emailSender.Messages.Count);
        var before = await db.Budgets.AsNoTracking().SingleAsync(x => x.Id == created.Budget.Id);
        var rows = await EmailRows();
        Assert.All(rows, x => Assert.Equal((1, BudgetNotification.Pending, "SMTP error (451)"), (x.Attempts, x.Status, x.LastError)));

        // A removed member is cancelled; disabling the channel cancels the rest; nothing changes the budget or VM.
        db.OrganizationMembers.Remove(await db.OrganizationMembers.SingleAsync(x => x.UserId == "dev"));
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromSeconds(60));
        await worker.RunOnceAsync(default);
        rows = await EmailRows();
        Assert.Equal(BudgetNotification.Cancelled, rows.Single(x => x.Recipient == "dev").Status);
        Assert.All(rows.Where(x => x.Recipient != "dev"), x => Assert.Equal((2, BudgetNotification.Failed), (x.Attempts, x.Status)));
        Assert.Equal(5, emailSender.Messages.Count);

        var budget = await db.Budgets.SingleAsync(x => x.Id == created.Budget.Id);
        await budgets.UpdateAsync(budget, new(budget.Revision, "Mail", 100m, [50], [], new(Email: false)), default);
        db.BudgetNotifications.Add(new BudgetNotification { Id = "queued", OrganizationId = "org", BudgetId = budget.Id,
            BudgetRevision = budget.Revision, Channel = BudgetNotification.Email, Recipient = "owner", Period = "2099-01",
            Threshold = 80, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
        await worker.RunOnceAsync(default);
        Assert.Equal(5, emailSender.Messages.Count);
        Assert.Equal(BudgetNotification.Cancelled, (await db.BudgetNotifications.AsNoTracking().SingleAsync(x => x.Id == "queued")).Status);
        var after = await db.Budgets.AsNoTracking().SingleAsync(x => x.Id == created.Budget.Id);
        Assert.Equal((before.AlertPeriod, before.AlertThresholdPercent), (after.AlertPeriod, after.AlertThresholdPercent));
        Assert.True((await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync()).ComputeReserved);

        Task<List<BudgetNotification>> EmailRows() =>
            db.BudgetNotifications.AsNoTracking().Where(x => x.Channel == BudgetNotification.Email).ToListAsync();
    }

    private void ConfigureBudgetEmail()
    {
        budgetEmail.Enabled = true;
        budgetEmail.Host = "smtp.example.test";
        budgetEmail.FromAddress = "budgets@example.test";
    }

    private async Task SeedBudgetMembers()
    {
        foreach (var (id, role) in new[] { ("owner", OrganizationRole.Owner), ("admin", OrganizationRole.Admin),
            ("dev", OrganizationRole.Developer) })
        {
            db.Users.Add(new User { Id = id, Email = $"{id}@example.test" });
            db.OrganizationMembers.Add(new OrganizationMember { Id = "member-" + id, OrganizationId = "org", UserId = id, Role = role });
        }
        await db.SaveChangesAsync();
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

    [Fact]
    public async Task BudgetWorkerAlertsWeeklyBudgetsOncePerIsoWeek()
    {
        var (budgets, worker, sender) = BudgetWorker();
        await SeedBudgetMembers();
        ConfigureBudgetEmail();
        await StartedBudgetWorkspace();
        await budgets.CreateAsync(new("org", "Weekly", 10m, null, [100],
            Notifications: new(Webhook: true, WebhookUrl: BudgetHook, Email: true), Period: BudgetPeriods.Week), "actor", default);

        await worker.RunOnceAsync(default);
        await worker.RunOnceAsync(default);
        var first = JsonDocument.Parse(Assert.Single(sender.Requests).Body).RootElement;
        Assert.Equal(("week", 10m, 17.27m, 74m), (first.GetProperty("periodType").GetString(), first.GetProperty("amount").GetDecimal(),
            first.GetProperty("projectedAmount").GetDecimal(), first.GetProperty("projectedMonthly").GetDecimal()));
        var week = BudgetPeriods.For(BudgetPeriods.Week, clock.GetUtcNow());
        Assert.Equal(week.Key, first.GetProperty("period").GetString());
        var email = emailSender.Messages[0];
        Assert.Contains($"for {week.Key}", email.Subject);
        Assert.Contains($"Period: {BudgetPeriods.Describe(week)}", email.TextBody);
        Assert.Contains("Budget amount: 10 USD per week", email.TextBody);
        Assert.Contains("Projected this week: 17.27 USD (172.7% of budget); monthly run rate 74 USD", email.TextBody);
        Assert.Contains("17.27 USD this week (74 USD per month)", email.TextBody);
        Assert.DoesNotContain("UTC month", email.TextBody);

        clock.Advance(TimeSpan.FromDays(7));
        await worker.RunOnceAsync(default);
        await worker.RunOnceAsync(default);
        Assert.Equal(2, sender.Requests.Count);
        var periods = (await db.BudgetNotifications.AsNoTracking().Where(x => x.Channel == BudgetNotification.InApp).ToListAsync())
            .Select(x => x.Period).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, periods.Distinct().Count());
        Assert.All(periods, x => Assert.Matches(@"^\d{4}-W\d{2}$", x));
        Assert.Equal(2, await db.AuditEvents.AsNoTracking().CountAsync(x => x.Action == BudgetService.ThresholdCrossedEvent));
    }

    [Fact]
    public void BudgetEmailRendersPayloadsQueuedBeforeBudgetPeriods()
    {
        var message = BudgetEmailContent.Render("owner@example.test", JsonSerializer.Serialize(new
        {
            budget = "Team", period = "2026-11", threshold = 80, currency = "USD", monthlyAmount = 100m, projectedMonthly = 85m,
            percentOfBudget = 85m, projectedHourly = .12m
        }), Budget.EmailAudienceAdmins);
        Assert.Contains("Period: 2026-11 (UTC calendar month)", message.TextBody);
        Assert.Contains("Budget amount: 100 USD per month", message.TextBody);
        Assert.Contains("Projected this month: 85 USD (85% of budget); hourly run rate 0.12 USD", message.TextBody);
    }

    private readonly BudgetEmailOptions budgetEmail = new();
    private readonly SyntheticEmailSender emailSender = new();

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
            .AddSingleton<IOptions<BudgetEmailOptions>>(Options.Create(budgetEmail)).AddSingleton<IBudgetEmailSender>(emailSender)
            .AddScoped<AuditEventService>().AddScoped<BudgetService>().AddScoped<BudgetNotificationDispatcher>()
            .BuildServiceProvider();
        var worker = new BudgetAlertWorker(provider.GetRequiredService<IServiceScopeFactory>(), clock, Options.Create(settings),
            NullLogger<BudgetAlertWorker>.Instance);
        return (ActivatorUtilities.CreateInstance<BudgetService>(provider), worker, sender);
    }

    private sealed class SyntheticEmailSender : IBudgetEmailSender
    {
        public List<BudgetEmailMessage> Messages { get; } = [];
        public BudgetEmailResult Result { get; set; } = new(true, null);

        public Task<BudgetEmailResult> SendAsync(BudgetEmailMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }
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
