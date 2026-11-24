using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Workspaces.Common.Services;

namespace Workspaces.Worker.Budgets;

public sealed record BudgetEmailMessage(string To, string Subject, string TextBody);

// Error is a short, secret-free description such as an SMTP status code; addresses and credentials are never included.
public sealed record BudgetEmailResult(bool Delivered, string? Error);

public interface IBudgetEmailSender
{
    Task<BudgetEmailResult> SendAsync(BudgetEmailMessage message, CancellationToken ct);
}

/// <summary>SMTP delivery with mandatory STARTTLS. The optional password is read from the secret store per send.</summary>
public sealed class SmtpBudgetEmailSender(ISecretStore secrets, IOptions<BudgetEmailOptions> options) : IBudgetEmailSender
{
    public async Task<BudgetEmailResult> SendAsync(BudgetEmailMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        using var client = new SmtpClient(settings.Host!, settings.Port)
        {
            EnableSsl = true, DeliveryMethod = SmtpDeliveryMethod.Network, UseDefaultCredentials = false
        };
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            var password = await secrets.GetAsync(
                new StoredSecretReference(OpenBaoSecretStore.ProviderName, settings.PasswordSecretPath!.Trim('/'), null), ct);
            client.Credentials = new NetworkCredential(settings.Username, password);
        }
        using var mail = new MailMessage(new MailAddress(settings.FromAddress!, settings.FromName), new MailAddress(message.To))
        {
            Subject = message.Subject, Body = message.TextBody, IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8
        };
        try
        {
            await client.SendMailAsync(mail, ct);
            return new(true, null);
        }
        catch (SmtpFailedRecipientException error) { return new(false, $"SMTP recipient rejected ({(int)error.StatusCode})"); }
        catch (SmtpException error) { return new(false, $"SMTP error ({(int)error.StatusCode})"); }
    }
}

/// <summary>
/// Plain-text email for a threshold alert, rendered only from the secret-free notification payload.
/// </summary>
public static class BudgetEmailContent
{
    public const string EstimateNotice =
        "These figures are estimates from recorded rates, not provider invoices or charges.";
    public const string InformOnlyNotice =
        "Budgets only inform: Municode never blocks, stops or resizes workspaces because of a budget.";

    public static BudgetEmailMessage Render(string to, string payloadJson, string audience)
    {
        var root = JsonDocument.Parse(payloadJson).RootElement;
        var currency = Text(root, "currency");
        var budget = Text(root, "budget");
        var period = Text(root, "period");
        var threshold = root.GetProperty("threshold").GetInt32();
        var (noun, description) = Period(root, period);
        var monthly = noun == BudgetPeriods.Month;
        // Payloads queued before budget periods existed carry only monthlyAmount and the monthly projection.
        var amount = root.TryGetProperty("amount", out _) ? "amount" : "monthlyAmount";
        var projected = root.TryGetProperty("projectedAmount", out _) ? "projectedAmount" : "projectedMonthly";
        var body = new StringBuilder();
        body.AppendLine("Municode budget alert (estimate)").AppendLine();
        body.AppendLine($"Budget: {budget}");
        body.AppendLine($"Period: {description}");
        var crossed = root.TryGetProperty("crossedThresholds", out var list)
            ? string.Join(", ", list.EnumerateArray().Select(x => $"{x.GetInt32()}%")) : $"{threshold}%";
        body.AppendLine($"Threshold reached: {threshold}% (newly crossed: {crossed})");
        body.AppendLine($"Budget amount: {Money(root, amount, currency)} per {noun}");
        body.AppendLine($"Projected this {noun}: {Money(root, projected, currency)} ({Number(root, "percentOfBudget")}% of budget); " +
            (monthly ? "" : $"monthly run rate {Money(root, "projectedMonthly", currency)}, ") +
            $"hourly run rate {Money(root, "projectedHourly", currency)}");
        if (root.TryGetProperty("complete", out var complete) && !complete.GetBoolean())
            body.AppendLine("The estimate is incomplete because some workspaces have no pricing.");

        if (root.TryGetProperty("contributors", out var contributors) && contributors.GetArrayLength() > 0)
        {
            body.AppendLine().AppendLine("Top contributors:");
            foreach (var item in contributors.EnumerateArray())
                body.AppendLine($"- {Text(item, "workspace")} ({Text(item, "providerAccountId")}, {Text(item, "region")}): " +
                    (monthly || !item.TryGetProperty("projectedAmount", out _)
                        ? $"{Money(item, "projectedMonthly", currency)} per month"
                        : $"{Money(item, "projectedAmount", currency)} this {noun} ({Money(item, "projectedMonthly", currency)} per month)") +
                    $", pricing {Text(item, "pricingStatus")}");
        }
        Section(body, root, "warnings", "Warnings:");
        Section(body, root, "assumptions", "Assumptions:");
        body.AppendLine().AppendLine(EstimateNotice).AppendLine(InformOnlyNotice);
        body.AppendLine().AppendLine(audience == Workspaces.Common.Models.Budget.EmailAudienceMembers
            ? "You receive this as a member of the organization."
            : "You receive this as an owner or admin of the organization.");
        body.AppendLine("Owners and admins can change notification settings in Settings > Budgets.");
        return new(to, SingleLine($"Budget alert: {budget} estimate reached {threshold}% for {period}", 200), body.ToString());
    }

    private static (string Noun, string Description) Period(JsonElement root, string key)
    {
        var type = Text(root, "periodType");
        if (!BudgetPeriods.IsValid(type) || !root.TryGetProperty("periodStart", out var start)
            || !start.TryGetDateTimeOffset(out var startsAt))
            return (BudgetPeriods.Month, $"{key} (UTC calendar month)");
        return (type, BudgetPeriods.Describe(BudgetPeriods.For(type, startsAt)));
    }

    private static void Section(StringBuilder body, JsonElement root, string name, string title)
    {
        if (!root.TryGetProperty(name, out var items) || items.GetArrayLength() == 0) return;
        body.AppendLine().AppendLine(title);
        foreach (var item in items.EnumerateArray()) body.AppendLine($"- {item.GetString()}");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? SingleLine(value.GetString()!, 200) : "";

    private static string Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture) : "?";

    private static string Money(JsonElement element, string name, string currency) => $"{Number(element, name)} {currency}";

    // Strips control characters so user-provided names cannot inject headers or reflow the message.
    private static string SingleLine(string value, int max)
    {
        var clean = new string(value.Select(x => char.IsControl(x) ? ' ' : x).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }
}
