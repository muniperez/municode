// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Municode.Cli.Commands;

namespace Municode.Cli.Seed;

internal sealed partial class SeedProvider : ISeedProvider
{
    private readonly string _provider;
    private readonly string _credential;
    private readonly HttpClient _http;
    private readonly Uri _origin;

    public SeedProvider(string provider, string credential, HttpMessageHandler? handler = null)
    {
        SeedGuard.Require(provider is "hetzner" or "digitalocean" or "vultr",
            "Unsupported seed provider.");
        SeedGuard.Require(!string.IsNullOrWhiteSpace(credential) && !credential.Any(char.IsWhiteSpace),
            "Provider API credential is invalid.");
        _provider = provider;
        _credential = credential;
        _origin = new Uri(provider switch
        {
            "hetzner" => "https://api.hetzner.cloud/v1/",
            "digitalocean" => "https://api.digitalocean.com/v2/",
            _ => "https://api.vultr.com/v2/"
        });
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ActivityHeadersPropagator = null
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = 8L * 1024 * 1024
        };
    }

    private string Servers => _provider switch
    {
        "hetzner" => "servers",
        "digitalocean" => "droplets",
        _ => "instances"
    };

    private string ServerObject => _provider switch
    {
        "hetzner" => "server",
        "digitalocean" => "droplet",
        _ => "instance"
    };

    public string CredentialScope(string operationId) => SeedGuard.CredentialScope(_credential, operationId);

    public async Task<string> GetAccountIdentityAsync(CancellationToken cancellationToken)
    {
        if (_provider == "hetzner")
        {
            await SendAsync(HttpMethod.Get, "servers?per_page=1", null, cancellationToken);
            return "Hetzner project bound to credential scope";
        }
        var account = (await SendAsync(HttpMethod.Get, "account", null, cancellationToken))
            .GetProperty("account");
        return _provider == "digitalocean"
            ? DigitalOceanIdentity(account)
            : "Vultr account " + SeedGuard.Hash(Text(account, "email"));
    }

    private static string DigitalOceanIdentity(JsonElement account)
    {
        if (account.TryGetProperty("team", out var team) && team.ValueKind != JsonValueKind.Null)
        {
            SeedGuard.Require(team.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String,
                "DigitalOcean team identity is missing; refusing an ambiguous account scope.");
            return "DigitalOcean team " + uuid.GetString();
        }
        SeedGuard.Require(account.TryGetProperty("uuid", out var accountUuid),
            "DigitalOcean account identity is missing.");
        return "DigitalOcean account " + accountUuid.GetString();
    }

    public async Task<SeedQuote> QuoteAsync(
        SeedSelection selection,
        string operationId,
        CancellationToken cancellationToken)
    {
        selection.Validate();
        var quote = _provider switch
        {
            "hetzner" => await HetznerQuoteAsync(selection, cancellationToken),
            "digitalocean" => await DigitalOceanQuoteAsync(selection, cancellationToken),
            _ => await VultrQuoteAsync(selection, cancellationToken)
        };
        SeedGuard.Require(quote.CpuCount >= 2 && quote.MemoryMiB >= 2048 && quote.DiskGiB >= 20,
            "Select a control-plane VPS with at least 2 CPUs, 2 GiB RAM and 20 GiB disk.");
        SeedGuard.Require(quote.Costs.Length > 0
            && quote.Costs.All(cost => cost.Monthly > 0 && cost.Currency.Length == 3
                && (cost.Hourly is null || cost.Hourly > 0)),
            "The provider did not return complete authoritative recurring pricing.");
        return quote with
        {
            CredentialScope = CredentialScope(operationId),
            RetrievedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<SeedServer?> FindServerAsync(string operationId, CancellationToken cancellationToken)
    {
        var matches = (await ListAsync(Servers, Servers, cancellationToken))
            .Where(value => Owned(value, operationId))
            .ToArray();
        SeedGuard.Require(matches.Length <= 1,
            "More than one operation-owned control-plane VPS exists; no duplicate or retry is safe.");
        return matches.Length == 0 ? null : Resource(matches[0], operationId);
    }

    public async Task<SeedServer> CreateServerAsync(
        SeedState state,
        string publicKey,
        CancellationToken cancellationToken)
    {
        SeedGuard.Require(publicKey.StartsWith("ssh-ed25519 ", StringComparison.Ordinal)
            && publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 3,
            "The generated SSH public key is invalid.");
        var selection = state.Plan.Selection;
        var quote = state.Plan.Quote;
        var name = Name(state.Id);
        object body = _provider switch
        {
            "hetzner" => new
            {
                name,
                location = selection.Region,
                server_type = selection.Size,
                image = long.Parse(quote.Image, CultureInfo.InvariantCulture),
                user_data = CloudInit(publicKey),
                labels = new Dictionary<string, string> { ["municode-workspaces-seed"] = state.Id },
                public_net = new { enable_ipv4 = true, enable_ipv6 = false },
                start_after_create = true
            },
            "digitalocean" => new
            {
                name,
                region = selection.Region,
                size = selection.Size,
                image = long.Parse(quote.Image, CultureInfo.InvariantCulture),
                user_data = CloudInit(publicKey),
                tags = new[] { name },
                ipv6 = false,
                backups = false,
                monitoring = false,
                vpc_uuid = quote.NetworkId
            },
            _ => new
            {
                label = name,
                hostname = name,
                region = selection.Region,
                plan = selection.Size,
                os_id = int.Parse(quote.Image, CultureInfo.InvariantCulture),
                user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(CloudInit(publicKey))),
                tags = new[] { name },
                enable_ipv6 = false,
                disable_public_ipv4 = false,
                enable_vpc = false,
                backups = "disabled",
                ddos_protection = false,
                activation_email = false,
                user_scheme = "root"
            }
        };
        var response = await SendAsync(HttpMethod.Post, Servers, body, cancellationToken);
        var created = response.GetProperty(ServerObject);
        SeedGuard.Require(Owned(created, state.Id),
            "Provider created a VPS without the exact seed ownership marker; reconcile before any retry.");
        return Resource(created, state.Id);
    }

    private static string CloudInit(string publicKey) => """
#cloud-config
users:
  - name: root
    shell: /bin/bash
    ssh_authorized_keys:
      - __PUBLIC_KEY__
disable_root: false
ssh_pwauth: false
""".Replace("__PUBLIC_KEY__", publicKey, StringComparison.Ordinal);

    private bool Owned(JsonElement value, string operationId) =>
        _provider == "hetzner"
            ? value.TryGetProperty("labels", out var labels)
                && labels.TryGetProperty("municode-workspaces-seed", out var id)
                && id.GetString() == operationId
            : value.TryGetProperty("tags", out var tags)
                && tags.EnumerateArray().Any(tag => tag.GetString() == Name(operationId));

    private SeedServer Resource(JsonElement value, string operationId)
    {
        string? address;
        if (_provider == "hetzner")
        {
            address = Text(value.GetProperty("public_net").GetProperty("ipv4"), "ip");
        }
        else if (_provider == "digitalocean")
        {
            var addresses = value.GetProperty("networks").GetProperty("v4")
                .EnumerateArray().Where(network => Text(network, "type") == "public").ToArray();
            SeedGuard.Require(addresses.Length <= 1, "DigitalOcean returned ambiguous public IPv4 addresses.");
            address = addresses.Length == 0 ? null : Text(addresses[0], "ip_address");
        }
        else
        {
            address = Text(value, "main_ip");
        }
        if (!IPAddress.TryParse(address, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Equals(IPAddress.Any))
            address = null;
        return _provider switch
        {
            "hetzner" => new(Text(value, "id"), operationId,
                Text(value.GetProperty("datacenter").GetProperty("location"), "name"),
                Text(value.GetProperty("server_type"), "name"), Text(value.GetProperty("image"), "id"), address),
            "digitalocean" => new(Text(value, "id"), operationId,
                Text(value.GetProperty("region"), "slug"), Text(value, "size_slug"),
                Text(value.GetProperty("image"), "id"), address),
            _ => new(Text(value, "id"), operationId, Text(value, "region"), Text(value, "plan"),
                Text(value, "os_id"), address)
        };
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_origin, path);
        SeedGuard.Require(uri.Scheme == Uri.UriSchemeHttps && uri.Host == _origin.Host
            && uri.AbsolutePath.StartsWith(_origin.AbsolutePath, StringComparison.Ordinal),
            "Provider pagination attempted to leave its authenticated HTTPS API origin.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            SeedGuard.Require(response.IsSuccessStatusCode,
                $"{_provider} API returned HTTP {(int)response.StatusCode}. "
                + "Check token scope, quota and catalog; response bodies and credentials are suppressed.");
            if (response.StatusCode == HttpStatusCode.NoContent || method == HttpMethod.Delete) return default;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException
                                      && !cancellationToken.IsCancellationRequested)
        {
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                $"{_provider} request outcome is unavailable. The CLI will not automatically retry a mutation; resume reconciles by operation identity.");
        }
    }

    private async Task<List<JsonElement>> ListAsync(
        string path,
        string field,
        CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        string? next = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "per_page=100";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            SeedGuard.Require(visited.Add(next) && visited.Count <= 1000,
                "Provider pagination is ambiguous; refusing an incomplete inventory.");
            var page = await SendAsync(HttpMethod.Get, next, null, cancellationToken);
            values.AddRange(page.GetProperty(field).EnumerateArray().Select(value => value.Clone()));
            next = null;
            if (_provider == "hetzner"
                && page.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("pagination", out var pagination)
                && pagination.TryGetProperty("next_page", out var nextPage)
                && nextPage.ValueKind == JsonValueKind.Number)
                next = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?")
                    + "per_page=100&page=" + nextPage.GetInt32().ToString(CultureInfo.InvariantCulture);
            if (_provider == "digitalocean"
                && page.TryGetProperty("links", out var links)
                && links.TryGetProperty("pages", out var pages)
                && pages.TryGetProperty("next", out var link))
                next = link.GetString();
            if (_provider == "vultr"
                && page.TryGetProperty("meta", out var vultrMeta)
                && vultrMeta.TryGetProperty("links", out var vultrLinks)
                && vultrLinks.TryGetProperty("next", out var cursor)
                && !string.IsNullOrEmpty(cursor.GetString()))
                next = path + "?per_page=100&cursor=" + Uri.EscapeDataString(cursor.GetString()!);
        }
        return values;
    }

    private static string Name(string operationId) => "municode-seed-" + operationId;
    private static string Text(JsonElement value, string property) => value.GetProperty(property).ToString();
    private static decimal Money(JsonElement value, string property) =>
        decimal.Parse(Text(value, property), CultureInfo.InvariantCulture);

    private static JsonElement One(
        IEnumerable<JsonElement> values,
        Func<JsonElement, bool> predicate,
        string failure)
    {
        var matches = values.Where(predicate).ToArray();
        SeedGuard.Require(matches.Length == 1, failure);
        return matches[0];
    }

    public void Dispose() => _http.Dispose();
}
