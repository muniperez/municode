namespace Municode.Common.Models;

/// <summary>
/// Month-to-date charges reported by a provider account's billing API. Only the aggregated amount is stored, never
/// invoice line items. A failed refresh keeps the last reported amount and records why the refresh failed.
/// </summary>
public sealed class ProviderCharge
{
    public const string Ok = "ok";
    public const string Failed = "failed";

    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    // UTC calendar month (yyyy-MM) the reported amount belongs to.
    public string Period { get; set; } = default!;
    public string Status { get; set; } = Ok;
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Source { get; set; }
    // When the provider generated the amount; the retrieval time when the provider does not say.
    public DateTimeOffset? AsOf { get; set; }
    public DateTimeOffset? RetrievedAt { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string? LastError { get; set; }
}
