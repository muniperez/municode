using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workspaces.Common.Providers.Common;
using System.Net.Http;

namespace Workspaces.Common.Providers.Hetzner;

public sealed partial class HetznerClient
{
    private readonly HttpClient _httpClient;
    private readonly HetznerOptions _options;
    private readonly ILogger<HetznerClient>? _logger;

    public CloudProvider Provider => CloudProvider.Hetzner;

    [ActivatorUtilitiesConstructor]
    public HetznerClient(
        HttpClient httpClient,
        IOptions<HetznerOptions> options,
        ILogger<HetznerClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public HetznerClient(
        HttpClient httpClient,
        HetznerOptions options,
        ILogger<HetznerClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }


    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                // Ignore read failures on error body
            }

            var redactedBody = TokenRedactor.Redact(body, _options.ApiToken);
            var message = $"Hetzner API request failed with HTTP {(int)response.StatusCode}.";
            _logger?.LogError("Hetzner API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }
}
