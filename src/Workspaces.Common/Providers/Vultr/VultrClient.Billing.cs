using Workspaces.Common.Providers.Common;

namespace Workspaces.Common.Providers.Vultr;

public sealed partial class VultrClient : IProviderChargesSource
{
    public const string ChargesSource = "vultr:account.pending_charges";

    // Read-only; the API key needs billing access. Pending charges are those accrued since the last invoice, in USD.
    public async Task<ProviderMonthToDateCharges> GetMonthToDateChargesAsync(CancellationToken ct)
    {
        using var doc = await VultrRequestAsync(HttpMethod.Get, "account", null, ct)
            ?? throw new ProviderApiException("Vultr account was not returned.", null, Provider);
        if (!doc.RootElement.TryGetProperty("account", out var account)
            || !account.TryGetProperty("pending_charges", out var pending)
            || !ProviderChargeValues.TryAmount(pending, out var amount))
            throw new ProviderApiException("Vultr account has no pending charges.", null, Provider);
        return new(amount, "USD", null, ChargesSource);
    }
}
