// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class SeedProviderTests
{
    [Theory]
    [InlineData("hetzner")]
    [InlineData("digitalocean")]
    [InlineData("vultr")]
    public async Task ProviderQuotesAndCreatesExactlyOneOwnedControlPlaneVps(string provider)
    {
        var handler = new ProviderHandler(provider);
        using var subject = new SeedProvider(provider, "synthetic-credential", handler);
        var selection = Selection(provider);
        var quote = await subject.QuoteAsync(selection, new string('a', 32), CancellationToken.None);

        Assert.Equal("linux/amd64", quote.Architecture);
        Assert.All(quote.Costs, cost => Assert.True(cost.Monthly > 0));
        var state = State(provider, quote);
        var server = await subject.CreateServerAsync(
            state, "ssh-ed25519 AAAAsynthetic municode-seed", CancellationToken.None);

        Assert.Equal(new string('a', 32), server.OperationId);
        Assert.Equal("203.0.113.40", server.Address);
        Assert.Single(handler.CreateBodies);
        Assert.Contains("user_data", handler.CreateBodies.Single());
        Assert.DoesNotContain("synthetic-credential", handler.CreateBodies.Single(), StringComparison.Ordinal);
        Assert.All(handler.RequestPaths, path =>
            Assert.DoesNotContain("ssh-keys", path, StringComparison.Ordinal));
        Assert.All(handler.RequestPaths, path =>
            Assert.DoesNotContain("account/keys", path, StringComparison.Ordinal));
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer synthetic-credential", value));
    }

    [Theory]
    [InlineData("provisioning", true)]
    [InlineData("root", true)]
    [InlineData("provision", false)]
    [InlineData("dns", false)]
    public async Task VultrQuoteRequiresTheProvisioningAcl(string acl, bool accepted)
    {
        var handler = new ProviderHandler("vultr") { VultrAcls = [acl] };
        using var subject = new SeedProvider("vultr", "synthetic-credential", handler);
        var quote = () => subject.QuoteAsync(Selection("vultr"), new string('a', 32), CancellationToken.None);

        if (accepted) Assert.Equal("linux/amd64", (await quote()).Architecture);
        else Assert.Contains("provisioning ACL", (await Assert.ThrowsAnyAsync<Exception>(quote)).Message);
    }

    [Fact]
    public async Task HetznerCreateAndFindReturnTheSameExactOperationAndImmutableFields()
    {
        var handler = new ProviderHandler("hetzner");
        using var subject = new SeedProvider("hetzner", "synthetic-credential", handler);
        var state = State("hetzner", await subject.QuoteAsync(
            Selection("hetzner"), new string('a', 32), CancellationToken.None));
        var created = await subject.CreateServerAsync(
            state, "ssh-ed25519 AAAAsynthetic municode-seed", CancellationToken.None);
        var found = await subject.FindServerAsync(state.Id, CancellationToken.None);

        Assert.Equal(new SeedServer("42", state.Id, "nbg1", "cx22", "101", "203.0.113.40"), created);
        Assert.Equal(created, found);
        Assert.Null(await subject.FindServerAsync(new string('b', 32), CancellationToken.None));
        Assert.Single(handler.CreateBodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HetznerRequiresExactOwnershipForCreateAndFind(bool create)
    {
        var handler = new ProviderHandler("hetzner");
        handler.HetznerServer["labels"]!["municode-seed"] = new string('a', 32) + "-other";
        using var subject = new SeedProvider("hetzner", "synthetic-credential", handler);
        if (create)
        {
            var state = State("hetzner", await subject.QuoteAsync(
                Selection("hetzner"), new string('a', 32), CancellationToken.None));
            var error = await Assert.ThrowsAsync<CliCommandException>(() => subject.CreateServerAsync(
                state, "ssh-ed25519 AAAAsynthetic municode-seed", CancellationToken.None));
            Assert.Contains("exact seed ownership marker", error.Message);
        }
        else
        {
            handler.HetznerServers.Add(handler.HetznerServer);
            Assert.Null(await subject.FindServerAsync(new string('a', 32), CancellationToken.None));
        }
    }

    [Fact]
    public async Task HetznerFindRefusesDuplicateOperationOwnedServers()
    {
        var handler = new ProviderHandler("hetzner");
        handler.HetznerServers.Add(handler.HetznerServer);
        var duplicate = (JsonObject)handler.HetznerServer.DeepClone();
        duplicate["id"] = 43;
        handler.HetznerServers.Add(duplicate);
        using var subject = new SeedProvider("hetzner", "synthetic-credential", handler);

        var error = await Assert.ThrowsAsync<CliCommandException>(() =>
            subject.FindServerAsync(new string('a', 32), CancellationToken.None));
        Assert.Contains("More than one operation-owned", error.Message);
        Assert.Empty(handler.CreateBodies);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    public async Task HetznerRefusesUnverifiableLocation(bool create, string shape)
    {
        var handler = new ProviderHandler("hetzner");
        handler.HetznerServer.Remove("location");
        if (shape != "missing")
            handler.HetznerServer["location"] = shape switch
            {
                "null" => null,
                "empty" => new JsonObject { ["name"] = "" },
                _ => JsonValue.Create("nbg1")
            };
        using var subject = new SeedProvider("hetzner", "synthetic-credential", handler);
        CliCommandException error;
        if (create)
        {
            var state = State("hetzner", await subject.QuoteAsync(
                Selection("hetzner"), new string('a', 32), CancellationToken.None));
            error = await Assert.ThrowsAsync<CliCommandException>(() => subject.CreateServerAsync(
                state, "ssh-ed25519 AAAAsynthetic municode-seed", CancellationToken.None));
            Assert.Single(handler.CreateBodies);
        }
        else
        {
            handler.HetznerServers.Add(handler.HetznerServer);
            error = await Assert.ThrowsAsync<CliCommandException>(() =>
                subject.FindServerAsync(new string('a', 32), CancellationToken.None));
            Assert.Empty(handler.CreateBodies);
        }
        Assert.Contains("without a location name", error.Message);
    }

    [Theory]
    [InlineData("location", "name", "hel1")]
    [InlineData("server_type", "name", "cx32")]
    [InlineData("image", "id", "102")]
    [InlineData("", "id", "43")]
    [InlineData("public_net", "ipv4", "203.0.113.41")]
    public async Task HetznerReconciliationRefusesChangedImmutableFields(
        string field, string property, string changed)
    {
        var handler = new ProviderHandler("hetzner");
        using var subject = new SeedProvider("hetzner", "synthetic-credential", handler);
        var state = State("hetzner", await subject.QuoteAsync(
            Selection("hetzner"), new string('a', 32), CancellationToken.None));
        state.ServerCreateStarted = true;
        state.Server = await subject.CreateServerAsync(
            state, "ssh-ed25519 AAAAsynthetic municode-seed", CancellationToken.None);
        if (field == "public_net")
            handler.HetznerServer[field]![property]!["ip"] = changed;
        else if (field.Length == 0)
            handler.HetznerServer[property] = int.Parse(changed);
        else if (property == "id")
            handler.HetznerServer[field]![property] = int.Parse(changed);
        else
            handler.HetznerServer[field]![property] = changed;

        var root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "artifacts", "seed-tests",
            "provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new SeedJournal(root, state.Id, true);
            var lifecycle = new SeedLifecycle(subject, new SeedHost(), journal, root, _ => { });
            var error = await Assert.ThrowsAsync<CliCommandException>(() =>
                lifecycle.ResumeAsync(state, null, null, CancellationToken.None));
            Assert.Contains(field == "public_net" ? "public IPv4 changed" : "immutable operation", error.Message);
            Assert.Single(handler.CreateBodies);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static SeedSelection Selection(string provider) => new(
        provider, provider == "vultr" ? "ewr" : "nbg1",
        provider == "digitalocean" ? "s-2vcpu-4gb" : provider == "vultr" ? "vc2-2c-4gb" : "cx22",
        "console.example.com", "api.example.com", "workspaces.example.net",
        "municode-v1.2.3");

    private static SeedState State(string provider, SeedQuote quote) => new()
    {
        Id = new string('a', 32),
        Plan = new(Selection(provider), quote, ReleaseFixture())
    };

    private static SeedReleaseIdentity ReleaseFixture() => new(
        "municode-v1.2.3", "municode", "https://github.com/muniperez/municode",
        new string('a', 40), "municode-1.2.3-linux-any.tar.gz",
        new string('b', 64), new string('c', 64), "github-sigstore");

    private sealed class ProviderHandler(string provider) : HttpMessageHandler
    {
        public List<string> CreateBodies { get; } = [];
        public List<string> RequestPaths { get; } = [];
        public List<string> Authorizations { get; } = [];
        public string[] VultrAcls { get; init; } = ["provisioning"];
        public List<JsonObject> HetznerServers { get; } = [];
        public JsonObject HetznerServer { get; } = (JsonObject)JsonSerializer.SerializeToNode(new
        {
            id = 42, labels = new Dictionary<string, string>
            {
                ["municode-seed"] = new string('a', 32)
            },
            location = new { name = "nbg1" },
            server_type = new { name = "cx22" }, image = new { id = 101 },
            public_net = new { ipv4 = new { ip = "203.0.113.40" } }
        })!;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.PathAndQuery);
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            if (request.Method == HttpMethod.Post)
            {
                CreateBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (provider == "hetzner") HetznerServers.Add(HetznerServer);
                return Json(CreatedResponse());
            }
            return Json(ResponseFor(request.RequestUri.PathAndQuery));
        }

        private object ResponseFor(string path)
        {
            if (provider == "hetzner")
            {
                return path switch
                {
                    var value when value.StartsWith("/v1/locations", StringComparison.Ordinal) =>
                        new { locations = new[] { new { id = 1, name = "nbg1" } } },
                    var value when value.StartsWith("/v1/server_types", StringComparison.Ordinal) =>
                        new
                        {
                            server_types = new[]
                            {
                                new { id = 1, name = "cx22", architecture = "x86", cores = 2,
                                    memory = 4, disk = 40, locations = new[] { new { name = "nbg1" } } }
                            }
                        },
                    var value when value.StartsWith("/v1/images", StringComparison.Ordinal) =>
                        new
                        {
                            images = new[]
                            {
                                new { id = 101, os_flavor = "ubuntu", os_version = "24.04",
                                    architecture = "x86", type = "system", status = "available" }
                            }
                        },
                    "/v1/pricing" => new
                    {
                        pricing = new
                        {
                            currency = "EUR",
                            server_types = new[]
                            {
                                new { id = 1, prices = new[] { new { location = "nbg1",
                                    price_monthly = new { net = "8.90" }, price_hourly = new { net = "0.01" } } } }
                            },
                            primary_ips = new[]
                            {
                                new { type = "ipv4", prices = new[] { new { location = "nbg1",
                                    price_monthly = new { net = "0.50" }, price_hourly = new { net = "0.001" } } } }
                            }
                        }
                    },
                    _ => new { servers = HetznerServers }
                };
            }
            if (provider == "digitalocean")
            {
                return path switch
                {
                    "/v2/account" => new
                    {
                        account = new { uuid = "account-1", status = "active", email_verified = true,
                            droplet_limit = 20 }
                    },
                    var value when value.StartsWith("/v2/droplets", StringComparison.Ordinal) =>
                        new { droplets = Array.Empty<object>() },
                    var value when value.StartsWith("/v2/vpcs", StringComparison.Ordinal) =>
                        new { vpcs = new[] { new { id = "vpc-1", region = "nbg1", @default = true } } },
                    var value when value.StartsWith("/v2/regions", StringComparison.Ordinal) =>
                        new { regions = new[] { new { slug = "nbg1", available = true } } },
                    var value when value.StartsWith("/v2/sizes", StringComparison.Ordinal) =>
                        new { sizes = new[] { new { slug = "s-2vcpu-4gb", available = true,
                            regions = new[] { "nbg1" }, vcpus = 2, memory = 4096, disk = 80,
                            price_monthly = "24.00", price_hourly = "0.0357" } } },
                    "/v2/images/ubuntu-24-04-x64" => new
                    {
                        image = new { id = 101, distribution = "Ubuntu", slug = "ubuntu-24-04-x64",
                            @public = true, regions = new[] { "nbg1" } }
                    },
                    _ => throw new InvalidOperationException(path)
                };
            }
            return path switch
            {
                "/v2/account" => new
                {
                    account = new { email = "operator@example.test", acls = VultrAcls }
                },
                var value when value.StartsWith("/v2/regions?per_page", StringComparison.Ordinal) =>
                    new { regions = new[] { new { id = "ewr" } } },
                var value when value.StartsWith("/v2/plans", StringComparison.Ordinal) =>
                    new
                    {
                        plans = new[]
                        {
                            new { id = "vc2-2c-4gb", type = "vc2", locations = new[] { "ewr" },
                                vcpu_count = 2, ram = 4096, disk = 80, monthly_cost = "24.00",
                                bandwidth = 3 }
                        }
                    },
                "/v2/regions/ewr/availability" =>
                    new { available_plans = new[] { "vc2-2c-4gb" } },
                var value when value.StartsWith("/v2/os", StringComparison.Ordinal) =>
                    new { os = new[] { new { id = 2284, family = "ubuntu", arch = "x64",
                        name = "Ubuntu 24.04 LTS" } } },
                _ => throw new InvalidOperationException(path)
            };
        }

        private object CreatedResponse() => provider switch
        {
            "hetzner" => new { server = HetznerServer },
            "digitalocean" => new
            {
                droplet = new
                {
                    id = 42, tags = new[] { "municode-seed-" + new string('a', 32) },
                    region = new { slug = "nbg1" }, size_slug = "s-2vcpu-4gb",
                    image = new { id = 101 },
                    networks = new { v4 = new[] { new { type = "public", ip_address = "203.0.113.40" } } }
                }
            },
            _ => new
            {
                instance = new
                {
                    id = "instance-42", tags = new[] { "municode-seed-" + new string('a', 32) },
                    region = "ewr", plan = "vc2-2c-4gb", os_id = 2284,
                    main_ip = "203.0.113.40"
                }
            }
        };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }
}
