// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Deploy;

internal sealed record DeployReleaseIdentity(
    string ReleaseId, string Tag, string SourceRevision, string BundleSha256, string Trust);

internal sealed record DeployRelease(DeployReleaseIdentity Identity, string ArchivePath, DateTimeOffset? PublishedAt);

internal sealed class DeployReleaseSource(HttpClient http, SeedRelease verifier)
{
    public const int Protocol = 1;
    internal const string LocalBuild = "local-build";

    public async Task<DeployRelease> ResolveAsync(
        string? tag, bool prerelease, string? bundleDirectory, string downloadDirectory, CancellationToken ct)
    {
        if (bundleDirectory is not null) return VerifyLocal(bundleDirectory);

        DateTimeOffset? publishedAt = null;
        if (tag == "latest") (tag, publishedAt) = await LatestAsync(prerelease, ct);
        var verified = await verifier.VerifyAsync(tag!, downloadDirectory, ct, Protocol);
        var identity = verified.Identity;
        return new(new(identity.Tag, identity.Tag, identity.SourceRevision, identity.BundleSha256, identity.Trust),
            Path.Combine(verified.Directory, identity.BundleName), publishedAt);
    }

    private async Task<(string Tag, DateTimeOffset PublishedAt)> LatestAsync(bool prerelease, CancellationToken ct)
    {
        (string Tag, DateTimeOffset PublishedAt)? selected = null;
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/muniperez/municode/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("municode-cli");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            SeedGuard.Require(response.IsSuccessStatusCode,
                "Could not resolve latest from the Municode GitHub releases API. Try an exact release tag.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            foreach (var release in document.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString();
                if (release.GetProperty("draft").GetBoolean()
                    || (!prerelease && release.GetProperty("prerelease").GetBoolean())
                    || tag is null || !SeedRelease.IsReleaseTag(tag)
                    || release.GetProperty("published_at").ValueKind != JsonValueKind.String
                    || !release.GetProperty("published_at").TryGetDateTimeOffset(out var published))
                    continue;
                if (selected is null || published > selected.Value.PublishedAt
                    || (published == selected.Value.PublishedAt && string.CompareOrdinal(tag, selected.Value.Tag) > 0))
                    selected = (tag, published);
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        return selected ?? throw new CliCommandException(CliExitCodes.ValidationError,
            "No eligible published Municode release was found.");
    }

    private static DeployRelease VerifyLocal(string directory)
    {
        var path = Path.Combine(directory, "release-manifest.json");
        SeedRelease.CheckLocalAsset(path, 128L * 1024 * 1024);
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        SeedRelease.RequireDeployProtocol(manifest, Protocol);
        var version = manifest.GetProperty("version").GetString();
        var revision = manifest.GetProperty("sourceRevision").GetString();
        var tag = "municode-v" + version;
        var bundle = $"municode-{version}-linux-any.tar.gz";
        SeedGuard.Require(SeedRelease.IsReleaseTag(tag)
            && manifest.GetProperty("schemaVersion").GetInt32() == 1
            && manifest.GetProperty("product").GetString() == "municode"
            && manifest.GetProperty("rid").GetString() == "linux-any"
            && manifest.GetProperty("sourceRepository").GetString() == "https://github.com/muniperez/municode"
            && manifest.GetProperty("bundle").GetString() == bundle
            && revision is not null && Regex.IsMatch(revision, @"\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z"),
            "The local build manifest has an invalid release identity or inexact source revision.");
        SeedGuard.Require(manifest.TryGetProperty("sourceDirty", out var dirty) && dirty.ValueKind == JsonValueKind.False,
            "Deploy requires a clean local build (sourceDirty: false).");
        SeedGuard.Require(manifest.GetProperty("imagePlatform").GetString() is "linux/amd64" or "linux/arm64",
            "The local build has an invalid image platform.");
        var images = manifest.GetProperty("imageArchives").EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
        SeedGuard.Require(images.Count == 3 && new[] { "api", "worker", "gateway" }.All(images.ContainsKey),
            "The local build image archive inventory is incomplete.");
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (component, image) in images)
        {
            var relative = $"images/{component}.tar";
            var id = image.GetProperty("imageId").GetString();
            var digest = image.GetProperty("sha256").GetString();
            SeedGuard.Require(id is not null && Regex.IsMatch(id, @"\Asha256:[a-f0-9]{64}\z")
                && image.GetProperty("path").GetString() == relative
                && image.GetProperty("reference").GetString() == $"municode-{component}:local-{id[7..]}"
                && DeployApproval.IsDigest(digest), "The local build has invalid image archive metadata.");
            digests.Add(relative, digest!);
        }
        var inventory = SeedRelease.VerifyInventory(directory, ["release-manifest.json", bundle], local: true);
        SeedGuard.Require(SeedRelease.Hash(bytes) == inventory["release-manifest.json"],
            "The local build manifest changed during verification.");
        SeedRelease.VerifyDependencies(manifest);
        SeedRelease.VerifyComponentsAndArchive(directory, manifest, bundle, deploy: true, digests,
            inventory["release-manifest.json"]);
        var sha = inventory[bundle];
        return new(new($"local-{revision![..12]}-{sha[..12]}", tag, revision, sha, LocalBuild),
            Path.Combine(directory, bundle), null);
    }
}
