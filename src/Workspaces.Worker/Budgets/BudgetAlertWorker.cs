using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workspaces.Common.Services;

namespace Workspaces.Worker.Budgets;

public static class BudgetAlertWorkerExtensions
{
    public static IServiceCollection AddBudgetAlertWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BudgetAlertWorkerOptions>(configuration.GetSection(BudgetAlertWorkerOptions.SectionName));
        services.AddScoped<AuditEventService>();
        services.AddScoped<BudgetService>();
        services.AddScoped<BudgetNotificationDispatcher>();
        services.AddSingleton<IBudgetWebhookSender, HttpBudgetWebhookSender>();
        services.Configure<BudgetEmailOptions>(configuration.GetSection(BudgetEmailOptions.SectionName));
        services.AddScoped<IBudgetEmailSender, SmtpBudgetEmailSender>();
        services.AddHttpClient(HttpBudgetWebhookSender.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider =>
                HttpBudgetWebhookSender.CreateHandler(provider.GetRequiredService<IOptions<BudgetAlertWorkerOptions>>().Value));
        services.AddHostedService<BudgetAlertWorker>();
        return services;
    }
}

/// <summary>
/// Evaluates budget thresholds in the background and delivers queued notifications. Budgets only inform:
/// nothing here creates, starts, stops or resizes resources.
/// </summary>
public sealed class BudgetAlertWorker(IServiceScopeFactory scopes, TimeProvider clock, IOptions<BudgetAlertWorkerOptions> options,
    ILogger<BudgetAlertWorker> logger) : BackgroundService
{
    private DateTimeOffset nextEvaluation = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Budget alert cycle failed ({ErrorType}); budget state and resources were not changed.", error.GetType().Name);
            }
            try { await Task.Delay(options.Value.DeliveryPoll, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    // Evaluates on the configured interval, then delivers anything due; delivery runs every poll so retries are timely.
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow() >= nextEvaluation)
        {
            await EvaluateAsync(ct);
            nextEvaluation = clock.GetUtcNow() + options.Value.EvaluationInterval;
        }
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BudgetNotificationDispatcher>().DeliverDueAsync(ct);
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        List<string> organizations;
        using (var scope = scopes.CreateScope())
            organizations = await scope.ServiceProvider.GetRequiredService<BudgetService>().OrganizationsWithBudgetsAsync(ct);
        foreach (var organization in organizations)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BudgetService>().EvaluateAlertsAsync(organization, ct);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Budget evaluation failed for one organization ({ErrorType}); other organizations continue.", error.GetType().Name);
            }
        }
    }
}
