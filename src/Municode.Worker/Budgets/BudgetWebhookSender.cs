using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Municode.Common.Services;

namespace Municode.Worker.Budgets;

public sealed record BudgetWebhookRequest(Uri Url, string Body, IReadOnlyDictionary<string, string> Headers);

// Error is a short, secret-free description; response bodies are never read or recorded.
public sealed record BudgetWebhookResult(bool Delivered, string? Error);

public interface IBudgetWebhookSender
{
    Task<BudgetWebhookResult> SendAsync(BudgetWebhookRequest request, CancellationToken ct);
}

public sealed class HttpBudgetWebhookSender(IHttpClientFactory clients) : IBudgetWebhookSender
{
    public const string HttpClientName = "budget-webhooks";

    public async Task<BudgetWebhookResult> SendAsync(BudgetWebhookRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json")
        };
        foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
        try
        {
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode ? new(true, null) : new(false, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "Timed out"); }
        catch (HttpRequestException ex) when (ex.InnerException is BudgetWebhookTargetException) { return new(false, "Target resolves to a non-public address"); }
        catch (HttpRequestException) { return new(false, "Connection failed"); }
    }

    public static SocketsHttpHandler CreateHandler(BudgetAlertWorkerOptions options) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, options.AllowPrivateWebhookTargets, ct)
    };

    // Resolve once and connect to the vetted address so DNS rebinding cannot redirect the request to a private host.
    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        if (addresses.Length == 0 || !allowPrivate && addresses.Any(x => !BudgetService.IsPublicAddress(x)))
            throw new BudgetWebhookTargetException();
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Webhook connection failed.", last);
    }
}

public sealed class BudgetWebhookTargetException() : Exception("Webhook targets must resolve to public addresses.");
