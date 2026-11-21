using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workspaces.Common.Providers.Common;
using System.Net.Http;
using System.Globalization;

namespace Workspaces.Common.Providers.Vultr;

public sealed partial class VultrClient
{
    private readonly HttpClient _httpClient;
    private readonly VultrOptions _options;
    private readonly ILogger<VultrClient>? _logger;

    public CloudProvider Provider => CloudProvider.Vultr;

    [ActivatorUtilitiesConstructor]
    public VultrClient(
        HttpClient httpClient,
        IOptions<VultrOptions> options,
        ILogger<VultrClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public VultrClient(
        HttpClient httpClient,
        VultrOptions options,
        ILogger<VultrClient>? logger = null)
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
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
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

            var redactedBody = TokenRedactor.Redact(body, _options.ApiKey);
            var message = $"Vultr API request failed with HTTP {(int)response.StatusCode}.";
            _logger?.LogError("Vultr API call failed: {Message}", message);
            throw new ProviderApiException(message, response.StatusCode, Provider, redactedBody);
        }
    }
}
