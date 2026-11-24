using System.Net.Mail;

namespace Workspaces.Common.Services;

/// <summary>
/// Operator-configured outbound email for budget notifications (section <c>BudgetAlerts:Email</c>). The SMTP password is
/// read from the secret store at <see cref="PasswordSecretPath"/>; it is never stored in configuration or the database.
/// </summary>
public sealed class BudgetEmailOptions
{
    public const string SectionName = "BudgetAlerts:Email";

    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Municode";
    public string? Username { get; set; }
    // Path of the password secret relative to the OpenBao KV mount, holding a "value" field.
    public string? PasswordSecretPath { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRecipientsPerBudget { get; set; } = 50;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 120));
    public int RecipientLimit => Math.Clamp(MaxRecipientsPerBudget, 1, 500);

    // Unconfigured or disabled email never queues or attempts delivery.
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65_535
        && MailAddress.TryCreate(FromAddress, out _)
        && (string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(PasswordSecretPath));
}
