// SPDX-License-Identifier: MPL-2.0

using System.Net.Http.Json;
using System.Text.Json;

namespace Municode.Cli.Seed;

internal sealed partial class SeedProvider
{
    private async Task<SeedQuote> HetznerQuoteAsync(
        SeedSelection selection,
        CancellationToken cancellationToken)
    {
        var location = One(await ListAsync("locations", "locations", cancellationToken),
            value => Text(value, "name") == selection.Region, "Unknown Hetzner region.");
        var size = One(await ListAsync("server_types", "server_types", cancellationToken),
            value => Text(value, "name") == selection.Size, "Unknown Hetzner size.");
        var architecture = Text(size, "architecture");
        SeedGuard.Require(architecture is "x86" or "arm", "Unsupported Hetzner server architecture.");
        if (size.TryGetProperty("locations", out var locations))
            SeedGuard.Require(locations.EnumerateArray().Any(value => Text(value, "name") == selection.Region),
                "The Hetzner size is not available in this region.");
        else
        {
            var datacenters = await ListAsync("datacenters", "datacenters", cancellationToken);
            SeedGuard.Require(datacenters.Any(datacenter =>
                    Text(datacenter.GetProperty("location"), "id") == Text(location, "id")
                    && datacenter.GetProperty("server_types").GetProperty("available")
                        .EnumerateArray().Any(id => id.ToString() == Text(size, "id"))),
                "The Hetzner size is not available in this region.");
        }
        var image = One(await ListAsync(
                "images?type=system&architecture=" + architecture, "images", cancellationToken),
            value => Text(value, "os_flavor") == "ubuntu" && Text(value, "os_version") == "24.04"
                && Text(value, "architecture") == architecture && Text(value, "type") == "system"
                && Text(value, "status") == "available",
            "No unique supported Ubuntu 24.04 Hetzner image is available.");
        var pricing = (await SendAsync(HttpMethod.Get, "pricing", null, cancellationToken))
            .GetProperty("pricing");
        var serverPrices = One(pricing.GetProperty("server_types").EnumerateArray(),
            value => Text(value, "id") == Text(size, "id"), "Hetzner server pricing is unavailable.");
        var serverPrice = One(serverPrices.GetProperty("prices").EnumerateArray(),
            value => Text(value, "location") == selection.Region, "Hetzner regional pricing is unavailable.");
        var ipv4 = One(pricing.GetProperty("primary_ips").EnumerateArray(),
            value => Text(value, "type") == "ipv4", "Hetzner IPv4 pricing is unavailable.");
        var ipv4Price = One(ipv4.GetProperty("prices").EnumerateArray(),
            value => Text(value, "location") == selection.Region, "Hetzner regional IPv4 pricing is unavailable.");
        var currency = Text(pricing, "currency");
        SeedCost Cost(string name, JsonElement price, string exclusions) =>
            new(name, Money(price.GetProperty("price_monthly"), "net"),
                Money(price.GetProperty("price_hourly"), "net"), currency,
                "hourly, monthly cap; net amount", exclusions, "https://api.hetzner.cloud/v1/pricing");
        return new("Hetzner project bound to credential scope", "",
            Text(image, "id"), architecture == "arm" ? "linux/arm64" : "linux/amd64",
            size.GetProperty("cores").GetInt32(), (int)(Money(size, "memory") * 1024),
            size.GetProperty("disk").GetInt32(),
            [
                Cost("one control-plane VPS", serverPrice,
                    "Taxes, excess traffic and optional backups/volumes excluded."),
                Cost("one auto-assigned public IPv4", ipv4Price,
                    "Deleted with the VPS; IPv6 is disabled.")
            ],
            default,
            "Project-scoped read/write API token required. Catalog availability is not a reservation.");
    }

    private async Task<SeedQuote> DigitalOceanQuoteAsync(
        SeedSelection selection,
        CancellationToken cancellationToken)
    {
        var account = (await SendAsync(HttpMethod.Get, "account", null, cancellationToken))
            .GetProperty("account");
        SeedGuard.Require(Text(account, "status") == "active" && account.GetProperty("email_verified").GetBoolean(),
            "The DigitalOcean account must be active and email-verified.");
        var droplets = await ListAsync("droplets", "droplets", cancellationToken);
        SeedGuard.Require(droplets.Count < account.GetProperty("droplet_limit").GetInt32(),
            "DigitalOcean Droplet quota is exhausted.");
        var network = One(await ListAsync("vpcs", "vpcs", cancellationToken),
            value => Text(value, "region") == selection.Region && value.GetProperty("default").GetBoolean(),
            "Select a region with an existing default DigitalOcean VPC; seed never creates a shared network.");
        var region = One(await ListAsync("regions", "regions", cancellationToken),
            value => Text(value, "slug") == selection.Region, "Unknown DigitalOcean region.");
        SeedGuard.Require(region.GetProperty("available").GetBoolean(), "DigitalOcean region is unavailable.");
        var size = One(await ListAsync("sizes", "sizes", cancellationToken),
            value => Text(value, "slug") == selection.Size, "Unknown DigitalOcean size.");
        SeedGuard.Require(size.GetProperty("available").GetBoolean()
            && size.GetProperty("regions").EnumerateArray().Any(value => value.GetString() == selection.Region),
            "The DigitalOcean size is unavailable in this region.");
        var image = (await SendAsync(
                HttpMethod.Get, "images/ubuntu-24-04-x64", null, cancellationToken))
            .GetProperty("image");
        SeedGuard.Require(Text(image, "distribution") == "Ubuntu" && Text(image, "slug") == "ubuntu-24-04-x64"
            && image.GetProperty("public").GetBoolean()
            && image.GetProperty("regions").EnumerateArray().Any(value => value.GetString() == selection.Region),
            "Official Ubuntu 24.04 x64 is unavailable in this DigitalOcean region.");
        var costs = new[]
        {
            new SeedCost("one control-plane Droplet with public IPv4",
                Money(size, "price_monthly"), Money(size, "price_hourly"), "USD",
                "hourly, monthly cap",
                "Taxes, bandwidth overage, backups and volumes excluded; IPv6 disabled.",
                "https://api.digitalocean.com/v2/sizes")
        };
        return new(DigitalOceanIdentity(account), "", Text(image, "id"), "linux/amd64",
            size.GetProperty("vcpus").GetInt32(), size.GetProperty("memory").GetInt32(),
            size.GetProperty("disk").GetInt32(), costs, default,
            "Read access to account/catalog/VPC inventory and Droplet create access are required. "
            + "Quota and availability checks are not reservations.", Text(network, "id"));
    }

    private async Task<SeedQuote> VultrQuoteAsync(
        SeedSelection selection,
        CancellationToken cancellationToken)
    {
        var account = (await SendAsync(HttpMethod.Get, "account", null, cancellationToken))
            .GetProperty("account");
        SeedGuard.Require(account.GetProperty("acls").EnumerateArray()
                .Any(value => value.GetString() is "provisioning" or "root"),
            "The Vultr key requires the provisioning ACL and must allow this workstation IP.");
        One(await ListAsync("regions", "regions", cancellationToken),
            value => Text(value, "id") == selection.Region, "Unknown Vultr region.");
        var size = One(await ListAsync("plans", "plans", cancellationToken),
            value => Text(value, "id") == selection.Size, "Unknown Vultr plan.");
        SeedGuard.Require(Text(size, "type") is "vc2" or "vhf" or "vhp" or "voc",
            "Choose a supported x64 Vultr cloud-compute family (vc2, vhf, vhp or voc).");
        SeedGuard.Require(size.GetProperty("locations").EnumerateArray()
                .Any(value => value.GetString() == selection.Region),
            "The Vultr plan is unavailable in this region.");
        var availability = await SendAsync(
            HttpMethod.Get, "regions/" + Uri.EscapeDataString(selection.Region) + "/availability",
            null, cancellationToken);
        SeedGuard.Require(availability.GetProperty("available_plans").EnumerateArray()
                .Any(value => value.GetString() == selection.Size),
            "Vultr regional capacity is unavailable.");
        var image = One(await ListAsync("os", "os", cancellationToken),
            value => Text(value, "family") == "ubuntu" && Text(value, "arch") == "x64"
                && System.Text.RegularExpressions.Regex.IsMatch(
                    Text(value, "name"), @"^Ubuntu 24\.04(?:\s|$)"),
            "No unique supported Vultr Ubuntu 24.04 x64 image is available.");
        var costs = new[]
        {
            new SeedCost("one control-plane instance with public IPv4",
                Money(size, "monthly_cost"), null, "USD",
                "hourly billing, 672-hour monthly cap; no hourly rate is inferred",
                "Taxes, regional bandwidth overage, backups and volumes excluded; IPv6 disabled.",
                "https://api.vultr.com/v2/plans")
        };
        return new("Vultr account " + SeedGuard.Hash(Text(account, "email")), "",
            Text(image, "id"), "linux/amd64", size.GetProperty("vcpu_count").GetInt32(),
            size.GetProperty("ram").GetInt32(), size.GetProperty("disk").GetInt32(),
            costs, default,
            "Provision ACL, active funded account and allowed workstation IP are required. "
            + "Availability is not a reservation; stopped VPSs remain billable.");
    }
}
