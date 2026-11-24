using System.Globalization;
using System.Text.Json;

namespace Workspaces.Common.Providers;

/// <summary>Month-to-date charges for the whole provider account, in the provider's billing currency.</summary>
public sealed record ProviderMonthToDateCharges(decimal Amount, string Currency, DateTimeOffset? AsOf, string Source);

/// <summary>Implemented by provider clients whose provider exposes a read-only billing endpoint.</summary>
public interface IProviderChargesSource
{
    Task<ProviderMonthToDateCharges> GetMonthToDateChargesAsync(CancellationToken ct);
}

public static class ProviderChargesSupport
{
    // Hetzner Cloud exposes no billing API, so its charges are shown as not available.
    public static bool IsSupported(CloudProvider provider) => provider is CloudProvider.DigitalOcean or CloudProvider.Vultr;
}

internal static class ProviderChargeValues
{
    private const decimal MaxAmount = 1_000_000_000m;

    // Providers return amounts as JSON numbers or decimal strings; anything else, negative or absurd is rejected.
    public static bool TryAmount(JsonElement value, out decimal amount)
    {
        amount = 0;
        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out amount),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out amount),
            _ => false
        };
        return parsed && amount is >= 0 and <= MaxAmount;
    }
}
