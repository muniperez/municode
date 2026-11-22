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

namespace Workspaces.Common.Providers.DigitalOcean;

public sealed partial class DigitalOceanClient
{
    private readonly HttpClient _httpClient;
    private readonly DigitalOceanOptions _options;
    private readonly ILogger<DigitalOceanClient>? _logger;

    public CloudProvider Provider => CloudProvider.DigitalOcean;

    [ActivatorUtilitiesConstructor]
    public DigitalOceanClient(
        HttpClient httpClient,
        IOptions<DigitalOceanOptions> options,
        ILogger<DigitalOceanClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public DigitalOceanClient(
        HttpClient httpClient,
        DigitalOceanOptions options,
        ILogger<DigitalOceanClient>? logger = null)
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
                // Ignore body read errors
            }

            var redactedBody = TokenRedactor.Redact(body, _options.ApiToken);
            var message = $"DigitalOcean API request failed with HTTP {(int)response.StatusCode}.";
            _logger?.LogError("DigitalOcean API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }
}
