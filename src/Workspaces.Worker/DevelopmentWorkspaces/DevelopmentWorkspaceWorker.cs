using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Data;
using Workspaces.Common.Services;

namespace Workspaces.Worker.DevelopmentWorkspaces;

public static class DevelopmentWorkspaceWorkerRegistration
{
    public static IServiceCollection AddDevelopmentWorkspaceWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevelopmentWorkspaceWorkerOptions>(configuration.GetSection(DevelopmentWorkspaceWorkerOptions.SectionName));
        services.AddSingleton<WorkspaceRuntimeTransport>();
        services.AddSingleton<IWorkspaceRuntimeTransport>(sp => sp.GetRequiredService<WorkspaceRuntimeTransport>());
        services.AddSingleton<WorkspaceTunnelManager>();
        services.AddSingleton<IWorkspaceTunnelManager>(sp => sp.GetRequiredService<WorkspaceTunnelManager>());
        services.AddHttpClient<GitHubAppClient>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<WorkspaceGitAccessService>();
        services.AddScoped<DevelopmentWorkspaceProcessor>();
        services.AddHostedService<DevelopmentWorkspaceWorker>();
        return services;
    }
}

public sealed class DevelopmentWorkspaceWorker(
    IServiceScopeFactory scopes, IOptions<DevelopmentWorkspaceOptions> feature,
    IOptions<DevelopmentWorkspaceWorkerOptions> options, IOptions<WorkspaceSshOptions> ssh,
    IOptions<DevelopmentWorkspaceGatewayOptions> gateway, ILogger<DevelopmentWorkspaceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!feature.Value.Enabled) return;

        var settings = options.Value;
        settings.Validate(ssh.Value, gateway.Value);
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var relayLock = new FileStream(settings.RelayLockFile, lockOptions);
        // SSH tunnels are process-owned. Fence persisted ports before another generation
        // can bind one of them, and allow outstanding gateway connections to drain.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            if (await InvalidateRelayPortsAsync(db, stoppingToken))
                await Task.Delay(settings.GatewayDrain(gateway.Value), stoppingToken);
        }

        await Task.WhenAll(LoopAsync(Loop.Process, stoppingToken), LoopAsync(Loop.Monitor, stoppingToken),
            LoopAsync(Loop.Filesystem, stoppingToken));
    }

    internal static async Task<bool> InvalidateRelayPortsAsync(WorkspaceDbContext db, CancellationToken ct)
    {
        var routes = db.DevelopmentWorkspaceGatewayRoutes;
        await routes.Where(route => route.RevokedAt == null && route.LoopbackPort != 0)
            .ExecuteUpdateAsync(set => set.SetProperty(route => route.LoopbackPort, 0), ct);
        // A revoked route can still have a connection attempt in flight, and a
        // previous process may have zeroed an active port before it drained.
        return await routes.AnyAsync(ct);
    }

    private enum Loop { Process, Monitor, Filesystem }

    private async Task LoopAsync(Loop loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceProcessor>();
                if (loop == Loop.Monitor) await processor.MonitorReadyAsync(ct);
                else if (loop == Loop.Filesystem) await processor.FilesystemNextAsync(ct);
                else await processor.ProcessNextAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Development workspace reconciliation failed ({ErrorType}); no response body or credential was logged.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 60)), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
