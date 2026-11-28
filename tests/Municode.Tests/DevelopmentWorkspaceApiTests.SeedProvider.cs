using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Municode.Api.Services;
using Municode.Common.Data;
using Municode.Common.Models;
using Municode.Common.Providers;
using Municode.Common.Services;

namespace Municode.Tests;

public sealed partial class DevelopmentWorkspaceApiTests
{
    private const string SeedCredential = "synthetic-provider-token";

    [Fact]
    public async Task StagedSeedProviderWaitsForSetupThenRegistersOnceInTheFirstOrganization()
    {
        using var directory = new SeedStagingDirectory();
        var staged = await BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "hetzner", Credential = SeedCredential }, directory.File);
        Assert.DoesNotContain(SeedCredential, JsonSerializer.Serialize(staged));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.File));

        var logs = new List<string>();
        Assert.Equal(SeedProviderRegistrationOutcome.WaitingForSetup, await RunRegistrarAsync(directory.File, logs));
        Assert.True(File.Exists(directory.File));

        await MarkSetupReadyAsync("other");
        Assert.Equal(SeedProviderRegistrationOutcome.Registered, await RunRegistrarAsync(directory.File, logs));
        Assert.False(File.Exists(directory.File));
        Assert.Equal(SeedProviderRegistrationOutcome.NothingStaged, await RunRegistrarAsync(directory.File, logs));

        await BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "hetzner", Credential = SeedCredential }, directory.File);
        Assert.Equal(SeedProviderRegistrationOutcome.AlreadyRegistered, await RunRegistrarAsync(directory.File, logs));
        Assert.False(File.Exists(directory.File));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var account = await db.ProviderAccounts.SingleAsync(x => x.OrganizationId == "other");
        Assert.Equal(CloudProvider.Hetzner, account.ProviderType);
        Assert.Equal(SeedProviderStaging.AccountName, account.Name);
        Assert.Equal("valid", account.ValidationStatus);
        Assert.Equal(SeedCredential, factory.Secrets.Values[account.CredentialPath!]);
        var audit = await db.AuditEvents.SingleAsync(x => x.OrganizationId == "other" && x.Action == "provider.created");
        Assert.Equal(SeedProviderRegistrar.Actor, audit.Actor);
        Assert.Empty(await db.DevelopmentWorkspaceVms.ToListAsync());
        Assert.DoesNotContain(logs, line => line.Contains(SeedCredential, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StagedSeedDefaultsRegisterTheDefaultProviderAndPlanOnceWithTheirSeededIds()
    {
        using var directory = new SeedStagingDirectory();
        var defaults = new SeedWorkspaceDefaults("provider_seed", "plan_seed", "fsn1", "cx32");
        var input = new BootstrapCommand.Input { ProviderType = "hetzner", Credential = SeedCredential, WorkspaceDefaults = defaults };
        await BootstrapCommand.StageProviderAsync(input, directory.File);
        await MarkSetupReadyAsync("other");
        var logs = new List<string>();
        Assert.Equal(SeedProviderRegistrationOutcome.Registered, await RunRegistrarAsync(directory.File, logs));
        Assert.False(File.Exists(directory.File));

        // A retry after a partial registration completes the missing plan without a second account.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
            db.ProviderPlanNames.RemoveRange(db.ProviderPlanNames.Where(x => x.PlanId == "plan_seed"));
            db.ProviderPlans.Remove(await db.ProviderPlans.SingleAsync(x => x.Id == "plan_seed"));
            await db.SaveChangesAsync();
        }
        await BootstrapCommand.StageProviderAsync(input, directory.File);
        Assert.Equal(SeedProviderRegistrationOutcome.AlreadyRegistered, await RunRegistrarAsync(directory.File, logs));
        await BootstrapCommand.StageProviderAsync(input, directory.File);
        Assert.Equal(SeedProviderRegistrationOutcome.AlreadyRegistered, await RunRegistrarAsync(directory.File, logs));

        await using var verify = factory.Services.CreateAsyncScope();
        var data = verify.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        var account = await data.ProviderAccounts.SingleAsync(x => x.OrganizationId == "other");
        Assert.Equal(("provider_seed", SeedProviderStaging.AccountName), (account.Id, account.Name));
        var plan = await data.ProviderPlans.SingleAsync(x => x.OrganizationId == "other");
        Assert.Equal(("plan_seed", "provider_seed", "fsn1", "cx32", "linux/amd64", true),
            (plan.Id, plan.ProviderAccountId, plan.Region, plan.Sku, plan.BuildPlatform, plan.Enabled));
        Assert.Equal(SeedProviderStaging.PlanName, plan.Name);
        Assert.Empty(await data.DevelopmentWorkspaceVms.ToListAsync());
        Assert.DoesNotContain(logs, line => line.Contains(SeedCredential, StringComparison.Ordinal));

        // The policy seed writes (CLI defaults plus the installer's host-clock stamps) must offer the seeded plan as
        // the default for new coding VMs with no further operator configuration.
        var now = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var json = $$"""
            {"DevelopmentWorkspaces":{"Currency":"EUR","VolumeMonthlyRatePerGb":0.044,"MaxPricingAgeDays":365,
             "PricingUpdatedAt":"{{now}}",
             "Profiles":{"standard":{"MemoryGiB":8,"CpuCount":4,"ComputeHourlyRate":0.0128,"AllowedPlanIds":["plan_seed"],
               "DefaultPlanIdsForNewWorkspaces":["plan_seed"],"AllowedSkus":["cx32"],"AllowedRegions":["fsn1"] } },
             "Images":{"ubuntu-24-04":{"DisplayName":"Ubuntu 24.04","Source":"ubuntu","ProviderImageId":"161547269",
               "Architecture":"linux/amd64","ProviderAccountId":"provider_seed","AllowedRegions":["fsn1"],"Enabled":true,
               "RuntimeQualified":true,"T3ConnectForNewWorkspaces":true,"QualifiedAt":"{{now}}" } } } }
            """;
        var policy = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build()
            .GetSection("DevelopmentWorkspaces").Get<DevelopmentWorkspaceOptions>()!;
        WorkspaceRecipe.Apply(policy);
        var service = new DevelopmentWorkspaceService(data, new IdGenerator(), TimeProvider.System, Options.Create(policy),
            Options.Create(new DevelopmentWorkspaceManagementOptions { ManagementAddresses = ["198.51.100.1/32"] }));
        var selections = await service.GetSelectionsAsync("other", null, CancellationToken.None);
        Assert.True(selections.Available, selections.UnavailableReason);
        var option = Assert.Single(selections.Options);
        Assert.Equal(("ubuntu-24-04", "standard", "plan_seed", "cx32", true),
            (option.ImageId, option.Profile, option.PlanId, option.Sku, option.IsDefault));

        var error = await Assert.ThrowsAsync<BootstrapException>(() => BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "hetzner", Credential = SeedCredential,
                WorkspaceDefaults = defaults with { PlanId = "provider_seed" } }, directory.File));
        Assert.Equal("seed_provider_invalid", error.Code);
    }

    [Fact]
    public async Task RejectedOrInvalidSeedProviderStagingIsDiscardedWithoutRegistration()
    {
        using var directory = new SeedStagingDirectory();
        await MarkSetupReadyAsync("other");
        var logs = new List<string>();
        await BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "hetzner", Credential = "rejected-provider-token" }, directory.File);
        Assert.Equal(SeedProviderRegistrationOutcome.Rejected, await RunRegistrarAsync(directory.File, logs));
        Assert.False(File.Exists(directory.File));

        await File.WriteAllTextAsync(directory.File, """{"providerType":"hetzner","credential":"x","extra":1}""");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(SeedProviderRegistrationOutcome.Rejected, await RunRegistrarAsync(directory.File, logs));
        Assert.False(File.Exists(directory.File));

        var error = await Assert.ThrowsAsync<BootstrapException>(() => BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "aws", Credential = SeedCredential }, directory.File));
        Assert.Equal("seed_provider_invalid", error.Code);
        error = await Assert.ThrowsAsync<BootstrapException>(() => BootstrapCommand.StageProviderAsync(
            new BootstrapCommand.Input { ProviderType = "hetzner", Credential = SeedCredential }, null));
        Assert.Equal("seed_provider_unavailable", error.Code);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        Assert.False(await db.ProviderAccounts.AnyAsync(x => x.OrganizationId == "other"));
        // Like the provider API, a probed-then-rejected candidate leaves a durable cleanup intent for its secret.
        var leftover = Assert.Single(factory.Secrets.Values.Keys);
        Assert.True(await db.ProviderCredentialCleanups.AnyAsync(x => x.Path == leftover));
        Assert.DoesNotContain(logs, line => line.Contains("rejected-provider-token", StringComparison.Ordinal));
    }

    private async Task MarkSetupReadyAsync(string organizationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        db.BootstrapRecords.Add(new BootstrapRecord
        {
            InstallationId = "installation", OperationId = "first-owner", State = "ready",
            OrganizationId = organizationId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<SeedProviderRegistrationOutcome> RunRegistrarAsync(string path, List<string> logs)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var registrar = new SeedProviderRegistrar(services.GetRequiredService<WorkspaceDbContext>(),
            services.GetRequiredService<ProviderAccountService>(), services.GetRequiredService<ProviderCatalogService>(),
            services.GetRequiredService<AuditEventService>(),
            new ListLogger(logs));
        return await registrar.RunAsync(path, CancellationToken.None);
    }

    private sealed class SeedStagingDirectory : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("municode-seed-provider-").FullName;
        public string File => Path.Combine(root, "provider.json");
        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class ListLogger(List<string> lines) : ILogger<SeedProviderRegistrar>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception) + exception);
    }
}
