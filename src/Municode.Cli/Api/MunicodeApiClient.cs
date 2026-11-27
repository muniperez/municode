// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Municode.Cli.Auth;
using Municode.Cli.Contracts;

namespace Municode.Cli.Api;

public sealed class MunicodeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly HttpClient _httpClient;

    public MunicodeApiClient(CliEnvironment environment, TokenStore tokenStore)
        : this(environment, tokenStore, new HttpClient()) { }

    public MunicodeApiClient(CliEnvironment environment, TokenStore tokenStore, HttpClient httpClient)
    {
        _environment = environment;
        _tokenStore = tokenStore;
        _httpClient = httpClient;
    }

    public Task<MeResponse> GetMeAsync(CancellationToken ct) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/api/me", null, ct);
    public Task<MeResponse> GetMeWithTokenAsync(string token, CancellationToken ct) =>
        SendAsync<MeResponse>(HttpMethod.Get, "/api/me", null, ct, token);
    public Task<CliLoginSessionCreateResponse> CreateCliLoginSessionAsync(CancellationToken ct) =>
        SendUnauthenticatedAsync<CliLoginSessionCreateResponse>(HttpMethod.Post, "/api/cli-login-sessions", null, ct);
    public Task<CliLoginSessionExchangeResponse?> ExchangeCliLoginSessionAsync(string sessionId, CancellationToken ct) =>
        SendUnauthenticatedMaybeAcceptedAsync<CliLoginSessionExchangeResponse>(HttpMethod.Post,
            $"/api/cli-login-sessions/{Uri.EscapeDataString(sessionId)}/exchange", null, ct);

    public Task<IReadOnlyList<DevelopmentWorkspaceResponse>> GetWorkspacesAsync(string organizationId, CancellationToken ct) =>
        SendAsync<IReadOnlyList<DevelopmentWorkspaceResponse>>(HttpMethod.Get,
            $"/api/development-workspaces?organizationId={Uri.EscapeDataString(organizationId)}", null, ct);
    public Task<DevelopmentWorkspaceResponse> GetWorkspaceAsync(string id, string organizationId, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Get,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}?organizationId={Uri.EscapeDataString(organizationId)}", null, ct);
    public Task<WorkspaceSelections> GetSelectionsAsync(string organizationId, string? workspaceId, CancellationToken ct) =>
        SendAsync<WorkspaceSelections>(HttpMethod.Get,
            $"/api/development-workspaces/selections?organizationId={Uri.EscapeDataString(organizationId)}"
            + (workspaceId is null ? "" : $"&workspaceId={Uri.EscapeDataString(workspaceId)}"), null, ct);
    public Task<DevelopmentWorkspaceResponse> CreateWorkspaceAsync(CreateDevelopmentWorkspaceRequest request, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Post, "/api/development-workspaces", request, ct);
    public Task<DevelopmentWorkspaceResponse> DeleteWorkspaceAsync(string id, string organizationId, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Delete,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}?organizationId={Uri.EscapeDataString(organizationId)}&confirmPermanentDeletion=true", null, ct);
    public Task<DevelopmentWorkspaceResponse> UpdateIdleTimeoutAsync(string id, string organizationId,
        UpdateDevelopmentWorkspaceIdleTimeoutRequest request, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Put,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/idle-timeout?organizationId={Uri.EscapeDataString(organizationId)}", request, ct);
    public Task<DevelopmentWorkspaceResponse> MutateWorkspaceAsync(string id, string organizationId, string action, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Post,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(action)}?organizationId={Uri.EscapeDataString(organizationId)}", null, ct);
    public Task<DevelopmentWorkspaceResponse> WorkspaceOperationAsync(
        string id, string organizationId, string path, object? body, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceResponse>(HttpMethod.Post,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/{path}?organizationId={Uri.EscapeDataString(organizationId)}", body, ct);
    public Task<IReadOnlyList<DevelopmentWorkspaceVmResponse>> GetWorkspaceVmsAsync(
        string id, string organizationId, CancellationToken ct) =>
        SendAsync<IReadOnlyList<DevelopmentWorkspaceVmResponse>>(HttpMethod.Get,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/vms?organizationId={Uri.EscapeDataString(organizationId)}", null, ct);
    public Task<DevelopmentWorkspaceGatewayLaunchResponse> LaunchGatewayAsync(
        string id, string vmId, string organizationId, long generation, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceGatewayLaunchResponse>(HttpMethod.Post,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/vms/{Uri.EscapeDataString(vmId)}/launch?organizationId={Uri.EscapeDataString(organizationId)}",
            new DevelopmentWorkspaceGatewayLaunchRequest(generation), ct);
    public Task<DevelopmentWorkspaceTaskLeaseResponse> WorkspaceTaskAsync(
        string id, string vmId, string organizationId, string taskId, bool complete, object body, CancellationToken ct) =>
        SendAsync<DevelopmentWorkspaceTaskLeaseResponse>(complete ? HttpMethod.Post : HttpMethod.Put,
            $"/api/development-workspaces/{Uri.EscapeDataString(id)}/vms/{Uri.EscapeDataString(vmId)}/tasks/{Uri.EscapeDataString(taskId)}/{(complete ? "complete" : "lease")}?organizationId={Uri.EscapeDataString(organizationId)}",
            body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, string? tokenOverride = null)
    {
        var token = tokenOverride ?? _tokenStore.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new ApiException(401, "missing_token",
                $"Set {CliEnvironment.TokenEnvironmentVariable} or run 'municode login --token <token>'.");
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendCoreAsync<T>(request, ct);
    }

    private async Task<T> SendUnauthenticatedAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        await SendCoreAsync<T>(Request(method, path, body), ct);

    private async Task<T?> SendUnauthenticatedMaybeAcceptedAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = Request(method, path, body);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Accepted) return default;
        return await ReadResponseAsync<T>(response, ct);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        _environment.EnsureApiConfigured();
        var request = new HttpRequestMessage(method, _environment.ApiBaseUrl + path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return request;
    }

    private async Task<T> SendCoreAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _httpClient.SendAsync(request, ct))
            return await ReadResponseAsync<T>(response, ct);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) await ThrowApiExceptionAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new ApiException((int)response.StatusCode, "empty_response", "Municode API returned an empty response.");
    }

    private static async Task ThrowApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, ct);
            if (error is not null)
                throw new ApiException((int)response.StatusCode, error.Error.Code, error.Error.Message, error.Error.Details);
        }
        catch (JsonException) { }
        throw new ApiException((int)response.StatusCode, "api_error",
            $"Municode API returned HTTP {(int)response.StatusCode}.");
    }
}
