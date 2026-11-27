using System.Globalization;
using System.Text.Json;
using Municode.Common.Providers.Common;

namespace Municode.Common.Providers.DigitalOcean;

public sealed partial class DigitalOceanClient : IProviderChargesSource
{
    public const string ChargesSource = "digitalocean:customers/my/balance";

    // Read-only; the token needs the billing:read scope. DigitalOcean bills in USD.
    public async Task<ProviderMonthToDateCharges> GetMonthToDateChargesAsync(CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Get, "customers/my/balance", null, ct)
            ?? throw new ProviderApiException("DigitalOcean balance was not returned.", null, Provider);
        var root = doc.RootElement;
        if (!root.TryGetProperty("month_to_date_usage", out var usage)
            || !ProviderChargeValues.TryAmount(usage, out var amount))
            throw new ProviderApiException("DigitalOcean balance has no month-to-date usage.", null, Provider);
        DateTimeOffset? asOf = root.TryGetProperty("generated_at", out var generated) && generated.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(generated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUniversalTime() : null;
        return new(amount, "USD", asOf, ChargesSource);
    }
}
