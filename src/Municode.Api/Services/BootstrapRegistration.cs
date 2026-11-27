using Municode.Common.Services;

namespace Municode.Api.Services;

public static class BootstrapRegistration
{
    public static IServiceCollection AddWorkspacesBootstrap(this IServiceCollection services)
    {
        services.AddScoped<BootstrapService>();
        services.AddHttpClient<IBootstrapIdentityStore, BootstrapKratosIdentityStore>(client =>
                client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false
            })
            .RemoveAllLoggers();
        return services;
    }
}
