// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

namespace Municode.Cli.Seed;

internal sealed partial class SeedProvider
{
    private static readonly string[] VultrFamilies = ["vc2", "vhf", "vhp", "voc"];

    // The cheapest 8 GiB, 2+ vCPU x64 plan in the seed region, launched from the provider's stock Ubuntu 24.04.
    private async Task<SeedWorkspaceDefaults> WorkspaceDefaultsAsync(
        SeedSelection selection,
        SeedQuote quote,
        CancellationToken cancellationToken)
    {
        var defaults = _provider switch
        {
            "hetzner" => await HetznerWorkspaceAsync(selection, quote, cancellationToken),
            "digitalocean" => await DigitalOceanWorkspaceAsync(selection, quote, cancellationToken),
            _ => await VultrWorkspaceAsync(selection, quote, cancellationToken)
        };
        SeedGuard.Require(defaults is not null,
            $"No 8 GiB x64 {_provider} plan with at least 2 vCPUs is available in {selection.Region} for the default "
            + "workspace plan. Choose another region or plan with --workspace-provider no.");
        SeedGuard.Require(defaults!.Hourly > 0 && defaults.Monthly > 0 && defaults.VolumeMonthlyRatePerGb > 0
                && defaults.Currency.Length == 3 && !string.IsNullOrWhiteSpace(defaults.Image),
            "The provider did not return complete pricing for the default workspace plan.");
        return defaults;
    }

    private async Task<SeedWorkspaceDefaults?> HetznerWorkspaceAsync(
        SeedSelection selection,
        SeedQuote quote,
        CancellationToken cancellationToken)
    {
        var location = One(await ListAsync("locations", "locations", cancellationToken),
            value => Text(value, "name") == selection.Region, "Unknown Hetzner region.");
        var types = await ListAsync("server_types", "server_types", cancellationToken);
        // Older API responses lack per-type locations; only then is datacenter availability needed.
        var datacenters = types.All(size => size.TryGetProperty("locations", out _))
            ? []
            : await ListAsync("datacenters", "datacenters", cancellationToken);
        bool Available(JsonElement size) => size.TryGetProperty("locations", out var locations)
            ? locations.EnumerateArray().Any(value => Text(value, "name") == selection.Region
                && (!value.TryGetProperty("deprecation", out var gone) || gone.ValueKind == JsonValueKind.Null))
            : datacenters.Any(datacenter => Text(datacenter.GetProperty("location"), "id") == Text(location, "id")
                && datacenter.GetProperty("server_types").GetProperty("available")
                    .EnumerateArray().Any(id => id.ToString() == Text(size, "id")));
        var sizes = types
            .Where(size => Text(size, "architecture") == "x86" && Money(size, "memory") == 8
                && size.GetProperty("cores").GetInt32() >= 2
                && (!size.TryGetProperty("deprecation", out var gone) || gone.ValueKind == JsonValueKind.Null)
                && Available(size))
            .ToList();
        var pricing = (await SendAsync(HttpMethod.Get, "pricing", null, cancellationToken)).GetProperty("pricing");
        var candidates = sizes.Select(size =>
            {
                var price = pricing.GetProperty("server_types").EnumerateArray()
                    .Where(value => Text(value, "id") == Text(size, "id"))
                    .SelectMany(value => value.GetProperty("prices").EnumerateArray())
                    .FirstOrDefault(value => Text(value, "location") == selection.Region);
                return (size, price);
            })
            .Where(x => x.price.ValueKind == JsonValueKind.Object)
            .OrderBy(x => Money(x.price.GetProperty("price_monthly"), "net"))
            .ThenByDescending(x => x.size.GetProperty("cores").GetInt32())
            .ThenBy(x => Text(x.size, "name"), StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) return null;
        var (chosen, cost) = candidates[0];
        var image = quote.Architecture == "linux/amd64"
            ? quote.Image
            : Text(One(await ListAsync("images?type=system&architecture=x86", "images", cancellationToken),
                value => Text(value, "os_flavor") == "ubuntu" && Text(value, "os_version") == "24.04"
                    && Text(value, "status") == "available",
                "No unique supported Ubuntu 24.04 x86 Hetzner image is available."), "id");
        return new(Text(chosen, "name"), chosen.GetProperty("cores").GetInt32(), 8, image,
            Money(cost.GetProperty("price_hourly"), "net"), Money(cost.GetProperty("price_monthly"), "net"),
            Money(pricing.GetProperty("volume").GetProperty("price_per_gb_month"), "net"),
            Text(pricing, "currency"), "https://api.hetzner.cloud/v1/pricing");
    }

    private async Task<SeedWorkspaceDefaults?> DigitalOceanWorkspaceAsync(
        SeedSelection selection,
        SeedQuote quote,
        CancellationToken cancellationToken)
    {
        var size = (await ListAsync("sizes", "sizes", cancellationToken))
            .Where(value => value.GetProperty("memory").GetInt32() == 8192 && value.GetProperty("vcpus").GetInt32() >= 2
                && value.GetProperty("available").GetBoolean() && !Text(value, "slug").Contains("gpu", StringComparison.Ordinal)
                && value.GetProperty("regions").EnumerateArray().Any(region => region.GetString() == selection.Region))
            .OrderBy(value => Money(value, "price_monthly"))
            .ThenByDescending(value => value.GetProperty("vcpus").GetInt32())
            .ThenBy(value => Text(value, "slug"), StringComparer.Ordinal)
            .FirstOrDefault();
        if (size.ValueKind != JsonValueKind.Object) return null;
        // DigitalOcean publishes no volume pricing API; this is the documented flat block storage rate.
        return new(Text(size, "slug"), size.GetProperty("vcpus").GetInt32(), 8, quote.Image,
            Money(size, "price_hourly"), Money(size, "price_monthly"), 0.10m, "USD",
            "https://api.digitalocean.com/v2/sizes; volumes https://www.digitalocean.com/pricing/volumes");
    }

    private async Task<SeedWorkspaceDefaults?> VultrWorkspaceAsync(
        SeedSelection selection,
        SeedQuote quote,
        CancellationToken cancellationToken)
    {
        var availability = await SendAsync(
            HttpMethod.Get, "regions/" + Uri.EscapeDataString(selection.Region) + "/availability",
            null, cancellationToken);
        var available = availability.GetProperty("available_plans").EnumerateArray()
            .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        var size = (await ListAsync("plans", "plans", cancellationToken))
            .Where(value => value.GetProperty("ram").GetInt32() == 8192 && value.GetProperty("vcpu_count").GetInt32() >= 2
                && VultrFamilies.Contains(Text(value, "type")) && available.Contains(Text(value, "id"))
                && value.GetProperty("locations").EnumerateArray().Any(region => region.GetString() == selection.Region))
            .OrderBy(value => Money(value, "monthly_cost"))
            .ThenByDescending(value => value.GetProperty("vcpu_count").GetInt32())
            .ThenBy(value => Text(value, "id"), StringComparer.Ordinal)
            .FirstOrDefault();
        if (size.ValueKind != JsonValueKind.Object) return null;
        var monthly = Money(size, "monthly_cost");
        var hourly = size.TryGetProperty("hourly_cost", out var rate) && rate.ValueKind == JsonValueKind.Number
            ? rate.GetDecimal()
            : Math.Ceiling(monthly / 672m * 1000m) / 1000m;
        // Vultr publishes no block storage pricing API; this is the documented NVMe (high_perf) block rate.
        var image = selection.Size == "vc2-1c-0.5gb"
            ? Text(await VultrOsAsync(false, cancellationToken), "id")
            : quote.Image;
        return new(Text(size, "id"), size.GetProperty("vcpu_count").GetInt32(), 8, image,
            hourly, monthly, 0.10m, "USD",
            "https://api.vultr.com/v2/plans; block storage https://www.vultr.com/pricing/#block-storage");
    }
}
