// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Municode.Cli.Commands;

namespace Municode.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "MUNICODE_TOKEN";
    public const string ApiUrlEnvironmentVariable = "MUNICODE_API_URL";

    private readonly string? _environmentApiUrl;

    private CliEnvironment(string? apiUrl, string configHome)
    {
        _environmentApiUrl = apiUrl;
        ConfigHome = configHome;
        ApiBaseUrl = NormalizeOptionalUrl(apiUrl) ?? LoadConfig()?.ApiBaseUrl ?? "";
    }

    public string ApiBaseUrl { get; private set; }
    public string ConfigHome { get; }
    public bool HasApiOverride => !string.IsNullOrWhiteSpace(_environmentApiUrl);
    public string ConfigFilePath => Path.Combine(ConfigHome, "config.json");
    public string TokenFilePath => Path.Combine(ConfigHome, "tokens",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiBaseUrl))).ToLowerInvariant() + ".token");

    public static CliEnvironment FromEnvironment()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new CliEnvironment(
            Environment.GetEnvironmentVariable(ApiUrlEnvironmentVariable),
            Path.Combine(configHome, "municode"));
    }

    public static CliEnvironment ForTests(string apiBaseUrl, string configHome) =>
        new(apiBaseUrl, configHome);

    public void SetApiBaseUrl(string value)
    {
        ApiBaseUrl = NormalizeUrl(value);
        Directory.CreateDirectory(ConfigHome);
        var temporary = ConfigFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CliConfig(ApiBaseUrl)));
            File.Move(temporary, ConfigFilePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void EnsureApiConfigured()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"An API URL is required. Run 'municode config set-api-url <url>' or set {ApiUrlEnvironmentVariable}.");
    }

    public static string NormalizeUrl(string value)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.HostNameType != UriHostNameType.Dns && uri.HostNameType != UriHostNameType.IPv4 &&
            uri.HostNameType != UriHostNameType.IPv6 ||
            uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new CliCommandException(CliExitCodes.ValidationError,
                "API URL must use HTTPS (HTTP is allowed only for localhost), with no path or credentials.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private CliConfig? LoadConfig()
    {
        if (!File.Exists(ConfigFilePath)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(ConfigFilePath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (config is null || string.IsNullOrWhiteSpace(config.ApiBaseUrl))
                throw new JsonException();
            config = config with { ApiBaseUrl = NormalizeUrl(config.ApiBaseUrl) };
            return config;
        }
        catch (Exception error) when (error is JsonException or IOException or CliCommandException)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, $"Invalid Municode CLI configuration: {ConfigFilePath}");
        }
    }

    private static string? NormalizeOptionalUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeUrl(value);

    private sealed record CliConfig(string ApiBaseUrl);
}
