// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Instances;
using Municode.Cli.Seed;

namespace Municode.Cli.Commands;

public sealed partial class CliApplication
{
    private readonly IConsole _console;
    private readonly CliEnvironment _environment;
    private readonly TokenStore _tokenStore;
    private readonly MunicodeApiClient _apiClient;
    private readonly SeedRuntime _seed;
    private readonly InstanceRegistry _instances;

    public CliApplication(
        IConsole console,
        CliEnvironment environment,
        TokenStore tokenStore,
        MunicodeApiClient apiClient)
        : this(console, environment, tokenStore, apiClient, new SeedRuntime())
    {
    }

    internal CliApplication(
        IConsole console,
        CliEnvironment environment,
        TokenStore tokenStore,
        MunicodeApiClient apiClient,
        SeedRuntime seed,
        ISeedProcess? instanceSsh = null)
    {
        _console = console;
        _environment = environment;
        _tokenStore = tokenStore;
        _apiClient = apiClient;
        _seed = seed;
        _instances = new InstanceRegistry(environment.ConfigHome, instanceSsh ?? SeedProcess.Default);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return CliExitCodes.Success;
        }
        if (args[0] is "-v" or "--version" or "version")
        {
            PrintVersion();
            return CliExitCodes.Success;
        }
        if (args[0] == "help")
        {
            var target = string.Join(" ", args.Skip(1).TakeWhile(x => x is not ("-h" or "--help")));
            return string.IsNullOrWhiteSpace(target) ? PrintHelpResult() : PrintCommandHelp(target);
        }
        if (args.Skip(1).Any(x => x is "-h" or "--help"))
            return PrintCommandHelp(string.Join(" ", args.TakeWhile(x => x is not ("-h" or "--help"))));

        try
        {
            return args[0] switch
            {
                "seed" => await RunSeedAsync(args.Skip(1).ToArray(), cancellationToken),
                "instance" => await RunInstanceAsync(args.Skip(1).ToArray(), cancellationToken),
                "config" => RunConfig(args.Skip(1).ToArray()),
                "token" => await RunTokenAsync(args.Skip(1).ToArray(), cancellationToken),
                "login" => await RunLoginAsync(args.Skip(1).ToArray(), cancellationToken),
                "workspaces" => await RunWorkspacesAsync(args.Skip(1).ToArray(), cancellationToken),
                _ => UnknownCommand(args[0])
            };
        }
        catch (ApiException error) when (error.StatusCode is 401 or 403 || error.Code == "missing_token")
        {
            _console.WriteError($"Auth error: {error.Message}");
            return CliExitCodes.AuthError;
        }
        catch (ApiException error) when (error.Code == "validation_failed")
        {
            _console.WriteError($"API error ({error.Code}): {error.Message}");
            foreach (var detail in error.Details) _console.WriteError($"{detail.Field}: {detail.Message}");
            return CliExitCodes.ValidationError;
        }
        catch (ApiException error)
        {
            _console.WriteError($"API error ({error.Code}): {error.Message}");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (HttpRequestException error)
        {
            _console.WriteError($"Network error: {error.Message}");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsSeedCommand(args))
            {
                _console.WriteLine("Seed interrupted. Durable operation state and billable provider resources are retained; "
                    + "use 'municode seed status <operation>' then resume with the original credential scope. Never start a replacement plan.");
                return CliExitCodes.ValidationError;
            }
            _console.WriteLine("Operation canceled.");
            return CliExitCodes.Success;
        }
        catch (TaskCanceledException)
        {
            _console.WriteError("Network error: request timed out.");
            return CliExitCodes.NetworkOrApiUnavailable;
        }
        catch (CliCommandException error)
        {
            _console.WriteError(error.Message);
            return error.ExitCode;
        }
    }

    private int RunConfig(string[] args)
    {
        if (args.Length == 0 || args[0] == "show")
        {
            _console.WriteLine("Municode CLI configuration");
            _console.WriteLine($"API URL: {(_environment.ApiBaseUrl.Length == 0 ? "not configured" : _environment.ApiBaseUrl)}");
            _console.WriteLine($"Config home: {_environment.ConfigHome}");
            if (_environment.HasApiOverride)
                _console.WriteLine($"{CliEnvironment.ApiUrlEnvironmentVariable} override: enabled");
            return CliExitCodes.Success;
        }
        if (args is ["set-api-url", _, ..])
        {
            _environment.SetApiBaseUrl(args[1]);
            _console.WriteLine($"API URL: {_environment.ApiBaseUrl}");
            return CliExitCodes.Success;
        }
        throw new CliCommandException(CliExitCodes.ValidationError,
            "Usage: municode config [show|set-api-url <url>]");
    }

    public static string GetVersion()
    {
        var assembly = typeof(CliApplication).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var version = informational.Split('+')[0];
            if (version is not ("0.0.0" or "1.0.0.0" or "")) return version;
        }
        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null ? "1.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    private void PrintVersion()
    {
        _console.WriteLine($"municode version {GetVersion()}");
        var source = typeof(CliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var match = System.Text.RegularExpressions.Regex.Match(source ?? "", @"(?:sha\.|\+)([0-9a-f]{40})(?:$|\.)");
        if (match.Success) _console.WriteLine("source " + match.Groups[1].Value);
    }

    private int PrintHelpResult() { PrintHelp(); return CliExitCodes.Success; }

    private void PrintHelp()
    {
        _console.WriteLine($"Municode CLI version {GetVersion()}");
        _console.WriteLine("Create, inspect, and control standalone development workspaces.");
        _console.WriteLine();
        _console.WriteLine("Usage:");
        _console.WriteLine("  municode <command> [arguments] [options]");
        _console.WriteLine();
        _console.WriteLine("Commands:");
        _console.WriteLine("  workspaces  Create, inspect, start, stop, and delete workspaces");
        _console.WriteLine("  seed        Plan, apply, and safely resume control-plane provisioning");
        _console.WriteLine("  instance    Manage locally registered control planes over pinned SSH");
        _console.WriteLine("  login       Log in through the browser or with an API token");
        _console.WriteLine("  token       Store an API authentication token");
        _console.WriteLine("  config      Show or update the Municode API URL");
        _console.WriteLine("  help        Show help for a command");
        _console.WriteLine("  version     Show the CLI version");
        _console.WriteLine();
        _console.WriteLine("Environment:");
        _console.WriteLine($"  {CliEnvironment.ApiUrlEnvironmentVariable}  Municode control-plane API URL");
        _console.WriteLine($"  {CliEnvironment.TokenEnvironmentVariable}     API token override");
        _console.WriteLine();
        _console.WriteLine("Run 'municode help workspaces' for the workspace command surface.");
    }

    private int PrintCommandHelp(string command)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case "config":
                _console.WriteLine("Usage: municode config [show|set-api-url <url>]");
                _console.WriteLine("Synthetic and localhost URLs are supported; non-local HTTP URLs are rejected.");
                return CliExitCodes.Success;
            case "login":
                _console.WriteLine("Usage: municode login [--token <token>] [--no-browser]");
                _console.WriteLine("Without --token, opens the control-plane browser approval flow.");
                return CliExitCodes.Success;
            case "token":
                _console.WriteLine("Usage: municode token set <token>");
                return CliExitCodes.Success;
            case "workspaces":
            case var workspace when workspace.StartsWith("workspaces ", StringComparison.Ordinal):
                PrintWorkspaceHelp();
                return CliExitCodes.Success;
            case "seed":
                PrintSeedHelp();
                return CliExitCodes.Success;
            case "instance" or "instance add" or "instance list" or "instance show" or "instance remove":
                PrintInstanceHelp();
                return CliExitCodes.Success;
            case "version":
                PrintVersion();
                return CliExitCodes.Success;
            case "help":
                PrintHelp();
                return CliExitCodes.Success;
            default:
                _console.WriteError($"Unknown command '{command}'.");
                PrintHelp();
                return CliExitCodes.ValidationError;
        }
    }

    private int UnknownCommand(string command)
    {
        _console.WriteError($"Unknown command '{command}'.");
        PrintHelp();
        return CliExitCodes.ValidationError;
    }

    private static bool IsSeedCommand(string[] args) => args.FirstOrDefault() == "seed";

    internal static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == name) return index + 1 < args.Count ? args[index + 1] : null;
            if (args[index].StartsWith(name + "=", StringComparison.Ordinal))
                return args[index][(name.Length + 1)..];
        }
        return null;
    }

    internal static bool TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else
                Process.Start("xdg-open", url);
            return true;
        }
        catch { return false; }
    }

    internal static bool IsTransientNetworkException(Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (error is OperationCanceledException or TaskCanceledException) return true;
        if (error is HttpRequestException or TimeoutException or IOException or SocketException) return true;
        if (error is ApiException apiError)
            return apiError.StatusCode is 408 or 429 or 500 or 502 or 503 or 504;
        return error.InnerException is not null && IsTransientNetworkException(error.InnerException, cancellationToken);
    }
}
