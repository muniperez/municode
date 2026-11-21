using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Workspaces.Api.Auth;
using Workspaces.Common.Data;
using Workspaces.Common.Contracts;
using Workspaces.Api.Providers;
using Workspaces.Api.Services;
using Workspaces.Common.Services;
using Workspaces.Common.Providers;
using Workspaces.Common.Providers.Common;
using Workspaces.Common.Providers.DigitalOcean;
using Workspaces.Common.Providers.Hetzner;
using Workspaces.Common.Providers.Vultr;

namespace Workspaces.Api.Extensions;

public static class WorkspacesApiServiceCollectionExtensions
{
    public static IServiceCollection AddWorkspacesApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddControllers(options =>
            {
                options.Conventions.Add(new RouteTokenTransformerConvention(new KebabCaseRouteTokenTransformer()));
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var details = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .SelectMany(entry => entry.Value!.Errors.Select(error =>
                        new ErrorDetail(NormalizeModelStateField(entry.Key), GetModelStateErrorMessage(error))))
                    .ToArray();

                return new BadRequestObjectResult(new ErrorResponse(new ErrorBody(
                    "validation_failed",
                    "Request body is invalid.",
                    details)));
            };
        });
        services.AddOpenApi();
        services.AddHealthChecks();
        services.AddCors(options =>
        {
            options.AddPolicy("Dashboard", policy =>
            {
                var origins = configuration
                    .GetSection("Cors:AllowedOrigins")
                    .Get<string[]>()
                    ?.Where(origin => !string.IsNullOrWhiteSpace(origin))
                    .ToArray();

                if (origins is { Length: > 0 })
                {
                    policy.WithOrigins(origins).AllowCredentials();
                }

                policy.AllowAnyHeader().AllowAnyMethod();
            });
        });

        services.Configure<KratosOptions>(
            configuration.GetSection(KratosOptions.SectionName));
        services.AddSingleton<AccountOriginValidator>();
        services.AddWorkspacesDatabase();
        services.AddSingleton<IConsoleUrlProvider, ConsoleUrlProvider>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, IdGenerator>();
        services.AddSingleton<IApiKeyTokenService, ApiKeyTokenService>();
        services.AddScoped<AuditEventService>();
        services.AddScoped<ControlPlanePrincipalResolver>();
        services.AddScoped<ControlPlaneAuthorization>();
        services.AddScoped<ProviderAccountClientFactory>();
        services.AddScoped<ProviderAccountService>();
        services.AddScoped<ProviderCatalogService>();
        services.AddScoped<WorkspaceGitAccessService>();
        services.AddScoped<DevelopmentWorkspaceService>();
        services.AddDevelopmentWorkspaceGateway(configuration);
        services.Configure<DevelopmentWorkspaceOptions>(configuration.GetSection(DevelopmentWorkspaceOptions.SectionName));
        services.Configure<HetznerOptions>(configuration.GetSection("Providers:Hetzner"));
        services.Configure<DigitalOceanOptions>(configuration.GetSection("Providers:DigitalOcean"));
        services.Configure<VultrOptions>(configuration.GetSection("Providers:Vultr"));
        services.AddHttpClient(ProviderAccountClientFactory.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateServiceHandler)
            .AddHttpMessageHandler(() => new ResilientHttpRetryHandler());
        services.AddHttpClient<IKratosSessionValidator, KratosSessionValidator>(client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.AddOptions<PortalSessionOptions>()
            .Bind(configuration.GetSection(PortalSessionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PortalSessionOptions>, PortalSessionOptionsValidator>();
        services.AddScoped<BrowserSessionService>();
        services.AddHttpClient<KratosBrowserFlowClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan) // The flow client bounds each operation, including response bodies.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });
        services.Configure<OpenBaoSecretStoreOptions>(configuration.GetSection(OpenBaoSecretStoreOptions.SectionName));
        services.AddHttpClient<ISecretStore, OpenBaoSecretStore>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider =>
                OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
        services.AddHttpClient(nameof(OpenBaoTokenRenewalService))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(provider =>
                OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
        services.AddHostedService<OpenBaoTokenRenewalService>();
        services.AddHostedService<ProviderCredentialCleanupWorker>();
        services.AddScoped<GitHubAppService>();
        services.AddHttpClient<GitHubAppClient>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(CreateServiceHandler);
        services
            .AddAuthentication(ControlPlaneAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ControlPlaneAuthenticationHandler>(ControlPlaneAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();

        return services;
    }

    private static HttpClientHandler CreateServiceHandler() =>
        new() { AllowAutoRedirect = false, UseCookies = false };

    private static string NormalizeModelStateField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return "body";
        }

        return field.StartsWith("$.", StringComparison.Ordinal) ? field[2..] : field;
    }

    private static string GetModelStateErrorMessage(ModelError error) =>
        string.IsNullOrWhiteSpace(error.ErrorMessage)
            ? "Invalid value."
            : error.ErrorMessage;
}

internal sealed partial class KebabCaseRouteTokenTransformer : IOutboundParameterTransformer
{
    public string? TransformOutbound(object? value) =>
        value is null ? null : KebabCaseRegex().Replace(value.ToString()!, "$1-$2").ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex KebabCaseRegex();
}
