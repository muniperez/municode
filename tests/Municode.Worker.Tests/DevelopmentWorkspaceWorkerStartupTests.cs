using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Municode.Common.Services;
using Municode.Worker.DevelopmentWorkspaces;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceWorkerStartupTests
{
    [Fact]
    public async Task UnconfiguredLocalWorkerSkipsWorkspaceProcessingWithoutFailingTheHost()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var worker = new DevelopmentWorkspaceWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DevelopmentWorkspaceWorkerOptions()), Options.Create(new WorkspaceSshOptions()),
            Options.Create(new DevelopmentWorkspaceGatewayOptions()), NullLogger<DevelopmentWorkspaceWorker>.Instance);

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public void BundledWorkerSettingsWithoutManagementAddressesPassStartupValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixture-ssh-" + Guid.NewGuid().ToString("N"));
        var key = WorkspaceBootstrap.Generate();
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var writer = new StreamWriter(new FileStream(path, fileOptions)))
                writer.Write(key.HostPrivateKey);
            // Mirrors packaging/bundle/compose.yml with the bundled empty management-address list.
            var settings = new DevelopmentWorkspaceWorkerOptions
            {
                ApiBaseUrl = "https://api.example.test", ManagementAddresses = [],
                RelayBindAddress = "172.31.21.2", RelayLockFile = "/var/lib/municode/relay.lock"
            };
            settings.Validate(new WorkspaceSshOptions { PublicKey = key.HostPublicKey, PrivateKeyFile = path },
                new DevelopmentWorkspaceGatewayOptions());
            Assert.False(settings.HasManagementAddresses());
            settings.ManagementAddresses = ["203.0.113.7/32"];
            Assert.True(settings.HasManagementAddresses());
        }
        finally { File.Delete(path); }
    }
}
