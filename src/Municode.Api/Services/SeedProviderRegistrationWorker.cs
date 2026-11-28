using Microsoft.Extensions.Options;
using Municode.Common.Services;

namespace Municode.Api.Services;

/// <summary>Completes an opted-in seed provider registration once first-owner setup is ready.</summary>
public sealed class SeedProviderRegistrationWorker(
    IServiceScopeFactory scopes,
    IOptions<SeedProviderRegistrationOptions> options,
    ILogger<SeedProviderRegistrationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = options.Value.StagingFile;
        if (string.IsNullOrWhiteSpace(path)) return;
        var delay = Interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SeedProviderRegistrar>().RunAsync(path, stoppingToken);
                }
                delay = Interval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Provider and secret-store failures can echo request details; record only the failure type.
                logger.LogWarning("Seed provider registration will retry after {FailureType}.", ex.GetType().Name);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumBackoff.Ticks));
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
