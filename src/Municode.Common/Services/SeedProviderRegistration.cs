using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Municode.Common.Contracts;
using Municode.Common.Data;
using Municode.Common.Providers;

namespace Municode.Common.Services;

public sealed class SeedProviderRegistrationOptions
{
    public const string SectionName = "SeedProviderRegistration";

    /// <summary>Protected file where seed stages its provider credential until first-owner setup completes.</summary>
    public string? StagingFile { get; set; }
}

public sealed record SeedProviderCredential(string ProviderType, string Credential,
    SeedWorkspaceDefaults? WorkspaceDefaults = null);

/// <summary>
/// The default workspace plan computed by seed. Its IDs match the account and plan that the seeded deployment policy
/// already references, so registration makes the default Ubuntu workspace path usable without further setup.
/// </summary>
public sealed record SeedWorkspaceDefaults(string ProviderAccountId, string PlanId, string Region, string Sku);

public enum SeedProviderRegistrationOutcome
{
    NothingStaged,
    WaitingForSetup,
    Registered,
    AlreadyRegistered,
    Rejected
}

/// <summary>
/// Stages the seed VPS provider credential on the control-plane host. The file is owner-only, replaced atomically
/// and removed as soon as registration completes or is rejected.
/// </summary>
public static class SeedProviderStaging
{
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public const string AccountName = "Default provider";
    public const string PlanName = "Default workspace plan";

    public static CloudProvider Validate(SeedProviderCredential value)
    {
        if (!ProviderAccountService.TryParseType(value.ProviderType, out var type))
            throw new InvalidDataException("Select Hetzner, DigitalOcean or Vultr.");
        if (string.IsNullOrWhiteSpace(value.Credential) || value.Credential.Length > 8192 || value.Credential.Any(char.IsControl))
            throw new InvalidDataException("The provider credential is empty, too long or contains control characters.");
        if (value.WorkspaceDefaults is { } defaults
            && (!Identifier(defaults.ProviderAccountId, "provider_") || !Identifier(defaults.PlanId, "plan_")
                || !CatalogId(defaults.Region) || !CatalogId(defaults.Sku)))
            throw new InvalidDataException("The seed workspace defaults are invalid.");
        return type;
    }

    private static bool Identifier(string? value, string prefix) =>
        value is { Length: <= 64 } && value.StartsWith(prefix, StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z]+_[a-z0-9]{1,48}$");

    private static bool CatalogId(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$");

    public static async Task WriteAsync(string path, SeedProviderCredential value, CancellationToken ct)
    {
        Validate(value);
        var pending = path + ".pending";
        File.Delete(pending);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(pending, options))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
        }
        File.Move(pending, path, true);
    }

    public static SeedProviderCredential? Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (file.LinkTarget is not null || file.Length > MaximumBytes
            || (!OperatingSystem.IsWindows() && (file.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0))
            throw new InvalidDataException("The staged seed provider file is not a small owner-only regular file.");
        var value = JsonSerializer.Deserialize<SeedProviderCredential>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("The staged seed provider file is empty.");
        Validate(value);
        return value;
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".pending");
    }
}

/// <summary>
/// Registers a staged seed provider credential as <c>Default provider</c> in the first owner's organization after setup,
/// plus the seed's default workspace plan when one was staged. Retries are idempotent: the deterministic account and
/// plan IDs are reused, and an organization that already has another account of the same provider type is left unchanged.
/// </summary>
public sealed class SeedProviderRegistrar(
    WorkspaceDbContext db,
    ProviderAccountService accounts,
    ProviderCatalogService catalog,
    AuditEventService audit,
    ILogger<SeedProviderRegistrar> logger)
{
    public const string Actor = "system:seed";

    public async Task<SeedProviderRegistrationOutcome> RunAsync(string path, CancellationToken ct)
    {
        SeedProviderCredential? staged;
        try { staged = SeedProviderStaging.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            SeedProviderStaging.Delete(path);
            logger.LogWarning("Discarded an invalid staged seed provider credential. Register the provider in organization settings.");
            return SeedProviderRegistrationOutcome.Rejected;
        }
        if (staged is null) return SeedProviderRegistrationOutcome.NothingStaged;
        var type = SeedProviderStaging.Validate(staged);
        var defaults = staged.WorkspaceDefaults;

        var ready = await db.BootstrapRecords.AsNoTracking()
            .Where(x => x.State == "ready" && x.OrganizationId != null).Select(x => x.OrganizationId!).ToListAsync(ct);
        if (ready.Count != 1 || !await db.Organizations.AnyAsync(x => x.Id == ready[0], ct))
            return SeedProviderRegistrationOutcome.WaitingForSetup;
        var organizationId = ready[0];

        var seeded = defaults is not null && await db.ProviderAccounts.AnyAsync(
            x => x.OrganizationId == organizationId && x.Id == defaults.ProviderAccountId && x.ProviderType == type, ct);
        if (!seeded && await db.ProviderAccounts.AnyAsync(x => x.OrganizationId == organizationId && x.ProviderType == type, ct))
        {
            SeedProviderStaging.Delete(path);
            logger.LogInformation("The first organization already has a {ProviderType} provider account; the seed credential was discarded.", type);
            return SeedProviderRegistrationOutcome.AlreadyRegistered;
        }

        try
        {
            if (!seeded)
            {
                var account = await accounts.CreateAsync(new CreateProviderRequest(organizationId, SeedProviderStaging.AccountName,
                    ProviderAccountService.WireType(type), staged.Credential), ct, defaults?.ProviderAccountId);
                await audit.AppendAsync(organizationId, Actor, "provider.created",
                    JsonSerializer.Serialize(new { providerAccountId = account.Id, account.Revision, source = "seed" }), ct);
            }
            if (defaults is not null && !await db.ProviderPlans.AnyAsync(x => x.Id == defaults.PlanId, ct))
            {
                var plan = await catalog.CreateAsync(new CreateProviderPlanRequest(organizationId, defaults.ProviderAccountId,
                    defaults.Region, defaults.Sku, SeedProviderStaging.PlanName), ct, defaults.PlanId);
                await audit.AppendAsync(organizationId, Actor, "plan.created",
                    JsonSerializer.Serialize(new { planId = plan.Id, plan.Revision, source = "seed" }), ct);
            }
        }
        catch (ProviderCatalogException ex)
        {
            SeedProviderStaging.Delete(path);
            logger.LogWarning("Seed provider registration was rejected ({Code}). Register the provider in organization settings.", ex.Code);
            return SeedProviderRegistrationOutcome.Rejected;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            SeedProviderStaging.Delete(path);
            logger.LogWarning("The provider rejected the seed credential (HTTP {Status}). Register the provider in organization settings.",
                (int)ex.StatusCode!.Value);
            return SeedProviderRegistrationOutcome.Rejected;
        }
        SeedProviderStaging.Delete(path);
        if (seeded)
        {
            logger.LogInformation("The seed {ProviderType} account was already registered; its default workspace plan is present.", type);
            return SeedProviderRegistrationOutcome.AlreadyRegistered;
        }
        logger.LogInformation("Registered the seed {ProviderType} account as the default workspace provider in the first organization.", type);
        return SeedProviderRegistrationOutcome.Registered;
    }
}
