// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;

namespace Municode.Cli.Seed;

internal sealed record SeedSelection(
    string Provider,
    string Region,
    string Size,
    string DashboardHostname,
    string ApiHostname,
    string GatewayDomain,
    string ReleaseTag)
{
    public void Validate()
    {
        SeedGuard.Require(Provider is "hetzner" or "digitalocean" or "vultr",
            "Choose exactly one supported provider: hetzner, digitalocean or vultr.");
        foreach (var value in new[] { Region, Size })
            SeedGuard.Require(Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$"),
                "Explicit --region and --size provider catalog IDs are required.");
        foreach (var host in new[] { DashboardHostname, ApiHostname, GatewayDomain })
            SeedGuard.Require(IsPublicHostname(host),
                "Dashboard, API and gateway inputs must be lowercase public DNS names, not URLs, IPs or wildcards.");
        SeedGuard.Require(new[] { DashboardHostname, ApiHostname, GatewayDomain }.Distinct(StringComparer.Ordinal).Count() == 3,
            "Dashboard, API and gateway DNS names must be distinct.");
        SeedGuard.Require(!Overlaps(GatewayDomain, DashboardHostname) && !Overlaps(GatewayDomain, ApiHostname),
            "The gateway suffix must not overlap the dashboard or API hostname.");
        SeedGuard.Require(Regex.IsMatch(ReleaseTag,
            @"^municode-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$"),
            "Select an exact municode-vMAJOR.MINOR.PATCH[-prerelease] release; never latest.");
    }

    private static bool IsPublicHostname(string value) =>
        value.Length <= 253 && value.Contains('.') && value == value.ToLowerInvariant()
        && value.Split('.').All(label => Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$"))
        && Uri.CheckHostName(value) == UriHostNameType.Dns
        && !new[] { "localhost", "local", "internal", "invalid", "test", "example" }.Any(value.EndsWith);

    private static bool Overlaps(string left, string right) =>
        left == right || left.EndsWith("." + right, StringComparison.Ordinal)
        || right.EndsWith("." + left, StringComparison.Ordinal);
}

internal sealed record SeedCost(
    string Resource,
    decimal Monthly,
    decimal? Hourly,
    string Currency,
    string Billing,
    string Exclusions,
    string Source);

internal sealed record SeedQuote(
    string Account,
    string CredentialScope,
    string Image,
    string Architecture,
    int CpuCount,
    int MemoryMiB,
    int DiskGiB,
    SeedCost[] Costs,
    DateTimeOffset RetrievedAt,
    string Restrictions,
    string? NetworkId = null);

internal sealed record SeedReleaseIdentity(
    string Tag,
    string Product,
    string Repository,
    string SourceRevision,
    string BundleName,
    string BundleSha256,
    string ManifestSha256,
    string Trust)
{
    public static SeedReleaseIdentity Validate(SeedReleaseIdentity identity, string expectedTag)
    {
        SeedGuard.Require(identity.Tag == expectedTag && identity.Product == "municode"
            && identity.Repository == "https://github.com/muniperez/municode"
            && Regex.IsMatch(identity.SourceRevision, "^[0-9a-f]{40}$")
            && Regex.IsMatch(identity.BundleSha256, "^[0-9a-f]{64}$")
            && Regex.IsMatch(identity.ManifestSha256, "^[0-9a-f]{64}$"),
            "Release manifest identity is unsupported or does not belong to this exact Municode release.");
        SeedGuard.Require(identity.BundleName == $"municode-{expectedTag["municode-v".Length..]}-linux-any.tar.gz",
            "Release bundle identity does not match the selected exact tag and standalone distribution.");
        return identity;
    }
}

internal sealed record SeedPlan(
    SeedSelection Selection,
    SeedQuote Quote,
    SeedReleaseIdentity Release,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceName = null)
{
    public string Approval
    {
        get
        {
            var stableQuote = Quote with { RetrievedAt = DateTimeOffset.UnixEpoch };
            if (InstanceName is not null)
                return SeedGuard.Hash(System.Text.Json.JsonSerializer.Serialize(
                    new { Selection, Quote = stableQuote, Release, InstanceName }, SeedJournal.Json));
            return SeedGuard.Hash(System.Text.Json.JsonSerializer.Serialize(
                new { Selection, Quote = stableQuote, Release }, SeedJournal.Json));
        }
    }
}

internal sealed record SeedServer(string Id, string OperationId, string Region, string Size, string Image, string? Address);

internal sealed class SeedState
{
    public int Schema { get; init; } = 1;
    public required string Id { get; init; }
    public required SeedPlan Plan { get; set; }
    public string Phase { get; set; } = "planned";
    public bool ServerCreateStarted { get; set; }
    public SeedServer? Server { get; set; }
    public string? HostFingerprint { get; set; }
    public bool HostPrepared { get; set; }
    public bool InstallStarted { get; set; }
    public bool HealthVerified { get; set; }
    public string? InstallationId { get; set; }
    public string? LastFailurePhase { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InstanceRegistered { get; set; }

    [JsonIgnore]
    public bool IsComplete => Phase == "complete";
}

internal interface ISeedProvider : IDisposable
{
    string CredentialScope(string operationId);
    Task<string> GetAccountIdentityAsync(CancellationToken cancellationToken);
    Task<SeedQuote> QuoteAsync(SeedSelection selection, string operationId, CancellationToken cancellationToken);
    Task<SeedServer?> FindServerAsync(string operationId, CancellationToken cancellationToken);
    Task<SeedServer> CreateServerAsync(
        SeedState state,
        string publicKey,
        CancellationToken cancellationToken);
}

internal interface ISeedHost
{
    Task CheckPrerequisitesAsync(CancellationToken cancellationToken);
    Task GenerateSshKeyAsync(string directory, CancellationToken cancellationToken);
    Task TrustAsync(SeedState state, string directory, string independentFingerprint, CancellationToken cancellationToken);
    Task PrepareAsync(SeedState state, string directory, CancellationToken cancellationToken);
    Task<bool> DnsReadyAsync(SeedState state, CancellationToken cancellationToken);
    Task InstallAsync(
        SeedState state,
        string directory,
        string releaseDirectory,
        CancellationToken cancellationToken);
    Task VerifyHttpsAndHealthAsync(SeedState state, CancellationToken cancellationToken);
}

internal sealed record SeedVerifiedRelease(SeedReleaseIdentity Identity, string Directory);

internal interface ISeedRelease
{
    Task<SeedVerifiedRelease> VerifyAsync(string tag, string directory, CancellationToken cancellationToken);
}

internal sealed class SeedRuntime
{
    public Func<string, string, HttpMessageHandler?, ISeedProvider> Providers { get; init; } =
        (provider, credential, handler) => new SeedProvider(provider, credential, handler);
    public Func<ISeedHost> Host { get; init; } = () => new SeedHost();
    public Func<ISeedRelease> Release { get; init; } = () => new SeedRelease();
}

internal static class SeedGuard
{
    public static void Require(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        string message)
    {
        if (!condition) throw new CliCommandException(CliExitCodes.ValidationError, message);
    }

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string CredentialScope(string credential, string operationId) =>
        Hash("municode-seed:" + operationId + ":" + credential);

    public static string SafeShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string FormatCost(SeedCost cost)
    {
        var recurring = cost.Monthly.ToString("0.##", CultureInfo.InvariantCulture) + " " + cost.Currency + "/month";
        var hourly = cost.Hourly is { } rate
            ? rate.ToString("0.####", CultureInfo.InvariantCulture) + " " + cost.Currency + "/hour; "
            : "";
        return $"{cost.Resource}: {hourly}{recurring} ({cost.Billing})";
    }
}
