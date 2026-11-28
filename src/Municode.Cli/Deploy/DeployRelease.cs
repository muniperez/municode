// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
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
        var local = SeedRelease.VerifyLocalBuild(directory, Protocol);
        return new(new(local.ReleaseId, local.Tag, local.SourceRevision, local.BundleSha256, LocalBuild),
            Path.Combine(directory, local.BundleName), null);
    }
}
