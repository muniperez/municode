namespace Municode.Worker.Budgets;

public sealed class BudgetAlertWorkerOptions
{
    public const string SectionName = "BudgetAlerts";

    public bool Enabled { get; set; } = true;
    public int EvaluationIntervalSeconds { get; set; } = 300;
    public int DeliveryPollSeconds { get; set; } = 30;
    public int MaxDeliveryAttempts { get; set; } = 6;
    public int RetryBaseSeconds { get; set; } = 60;
    public int RetryMaxSeconds { get; set; } = 3600;
    public int WebhookTimeoutSeconds { get; set; } = 10;
    public int DeliveryBatchSize { get; set; } = 25;
    // Self-hosted installations may deliver to an internal relay; public targets only by default.
    public bool AllowPrivateWebhookTargets { get; set; }

    public TimeSpan EvaluationInterval => TimeSpan.FromSeconds(Math.Clamp(EvaluationIntervalSeconds, 30, 86_400));
    public TimeSpan DeliveryPoll => TimeSpan.FromSeconds(Math.Clamp(DeliveryPollSeconds, 1, 600));
    public int MaxAttempts => Math.Clamp(MaxDeliveryAttempts, 1, 20);
    public TimeSpan WebhookTimeout => TimeSpan.FromSeconds(Math.Clamp(WebhookTimeoutSeconds, 1, 60));

    public TimeSpan RetryDelay(int attempt)
    {
        var baseSeconds = Math.Clamp(RetryBaseSeconds, 1, 86_400);
        var maxSeconds = Math.Max(baseSeconds, Math.Clamp(RetryMaxSeconds, 1, 86_400));
        return TimeSpan.FromSeconds(Math.Min(maxSeconds, baseSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))));
    }
}
