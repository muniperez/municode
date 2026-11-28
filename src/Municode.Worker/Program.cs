using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Municode.Common.Data;
using Municode.Common.Extensions;
using Municode.Common.Providers;
using Municode.Common.Providers.Common;
using Municode.Common.Providers.DigitalOcean;
using Municode.Common.Providers.Hetzner;
using Municode.Common.Providers.Vultr;
using Municode.Common.Services;
using Municode.Worker.Budgets;
using Municode.Worker.DevelopmentWorkspaces;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddDevelopmentWorkspaceConfiguration();
var services = builder.Services;
var configuration = builder.Configuration;

services.AddMunicodeDatabase();
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IIdGenerator, IdGenerator>();
services.AddScoped<DevelopmentWorkspaceService>();
services.AddScoped<ProviderAccountClientFactory>();
services.Configure<DevelopmentWorkspaceOptions>(configuration.GetSection(DevelopmentWorkspaceOptions.SectionName));
services.Configure<DevelopmentWorkspaceGatewayOptions>(configuration.GetSection(DevelopmentWorkspaceGatewayOptions.SectionName));
services.Configure<WorkspaceSshOptions>(configuration.GetSection(WorkspaceSshOptions.SectionName));
services.Configure<HetznerOptions>(configuration.GetSection("Providers:Hetzner"));
services.Configure<DigitalOceanOptions>(configuration.GetSection("Providers:DigitalOcean"));
services.Configure<VultrOptions>(configuration.GetSection("Providers:Vultr"));
services.AddHttpClient(ProviderAccountClientFactory.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .AddHttpMessageHandler(() => new ResilientHttpRetryHandler());
services.Configure<OpenBaoSecretStoreOptions>(configuration.GetSection(OpenBaoSecretStoreOptions.SectionName));
services.AddHttpClient<ISecretStore, OpenBaoSecretStore>().RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(provider =>
        OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
services.AddHttpClient(nameof(OpenBaoTokenRenewalService)).RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(provider =>
        OpenBaoHttpHandler.Create(provider.GetRequiredService<IOptions<OpenBaoSecretStoreOptions>>().Value));
services.AddHostedService<OpenBaoTokenRenewalService>();
services.AddDevelopmentWorkspaceWorker(configuration);
services.AddBudgetAlertWorker(configuration);

await builder.Build().RunAsync();
