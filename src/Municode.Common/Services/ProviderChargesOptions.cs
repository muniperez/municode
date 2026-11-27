namespace Municode.Common.Services;

/// <summary>Worker retrieval of provider-reported charges. Read-only billing calls; never changes resources.</summary>
public sealed class ProviderChargesOptions
{
    public const string SectionName = "BudgetAlerts:ProviderCharges";

    public bool Enabled { get; set; } = true;
    public int RefreshMinutes { get; set; } = 60;
    public int StaleAfterMinutes { get; set; } = 180;

    public TimeSpan Refresh => TimeSpan.FromMinutes(Math.Clamp(RefreshMinutes, 15, 1440));
    public TimeSpan StaleAfter => TimeSpan.FromMinutes(Math.Max(Math.Clamp(StaleAfterMinutes, 30, 10_080), Refresh.TotalMinutes));
}
