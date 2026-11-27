// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Api;
using Municode.Cli.Contracts;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunTokenAsync(string[] args, CancellationToken ct)
    {
        if (args is not ["set", _, ..] || string.IsNullOrWhiteSpace(args[1]))
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: municode token set <token>");
        var token = args[1];
        await _apiClient.GetMeWithTokenAsync(token, ct);
        _tokenStore.SaveToken(token);
        _console.WriteLine("Token stored.");
        return CliExitCodes.Success;
    }

    private async Task<int> RunLoginAsync(string[] args, CancellationToken ct)
    {
        var token = GetOption(args, "--token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            var me = await _apiClient.GetMeWithTokenAsync(token, ct);
            _tokenStore.SaveToken(token);
            _console.WriteLine($"Logged in as {me.Email}");
            if (me.Organizations.Count > 0) _console.WriteLine($"Organization: {me.Organizations[0].Name}");
            return CliExitCodes.Success;
        }
        var session = await _apiClient.CreateCliLoginSessionAsync(ct);
        if (!args.Contains("--no-browser", StringComparer.Ordinal) && TryOpenBrowser(session.LoginUrl))
            _console.WriteLine("Opening browser for Municode login...");
        else
        {
            _console.WriteLine("Open this URL to finish Municode login:");
            _console.WriteLine(session.LoginUrl);
        }
        var exchange = await PollCliLoginExchangeAsync(session.SessionId, session.ExpiresAt, ct);
        _tokenStore.SaveToken(exchange.Token);
        _console.WriteLine($"Logged in as {exchange.Email}");
        _console.WriteLine($"Organization: {exchange.Organization.Name}");
        _console.WriteLine($"Token: {MaskToken(exchange.Token)}");
        return CliExitCodes.Success;
    }

    private async Task<CliLoginSessionExchangeResponse> PollCliLoginExchangeAsync(
        string sessionId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                var exchange = await _apiClient.ExchangeCliLoginSessionAsync(sessionId, ct);
                if (exchange is not null) return exchange;
            }
            catch (Exception error) when (IsTransientNetworkException(error, ct)) { continue; }
            _console.WriteLine("Waiting for browser approval...");
        }
        throw new ApiException(401, "cli_login_session_expired", "CLI login session expired before approval.");
    }

    private static string MaskToken(string token)
    {
        var visibleLength = Math.Min("mncd_live_123456".Length, token.Length);
        return token[..visibleLength] + "...";
    }
}
