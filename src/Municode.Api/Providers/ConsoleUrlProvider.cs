namespace Municode.Api.Providers;

public sealed class ConsoleUrlProvider(IConfiguration configuration, IWebHostEnvironment environment) : IConsoleUrlProvider
{
    public string GetConsoleBaseUrl()
    {
        var configured = configuration["Console:BaseUrl"] ?? configuration["Dashboard:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return environment.IsDevelopment() ? "http://localhost:3200" : throw new InvalidOperationException("Configure Console:BaseUrl for browser and CLI sign-in.");
    }
}
