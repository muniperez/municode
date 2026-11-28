// SPDX-License-Identifier: MPL-2.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Municode.Cli.Commands;
using Municode.Cli.Deploy;

namespace Municode.Cli.Seed;

internal sealed record LocalBuildRelease(
    string ReleaseId, string Tag, string SourceRevision, string BundleName, string BundleSha256, string ManifestSha256);

internal sealed class SeedRelease(
    ISeedProcess? runner = null,
    HttpMessageHandler? handler = null) : ISeedRelease
{
    private readonly ISeedProcess _process = runner ?? SeedProcess.Default;
    private readonly HttpMessageHandler? _handler = handler;

    public Task<SeedVerifiedRelease> VerifyAsync(
        string tag,
        string directory,
        CancellationToken cancellationToken) => VerifyAsync(tag, directory, cancellationToken, deployProtocol: null);

    internal async Task<SeedVerifiedRelease> VerifyAsync(
        string tag, string directory, CancellationToken cancellationToken, int? deployProtocol)
    {
        SeedGuard.Require(IsReleaseTag(tag),
            $"{(deployProtocol is null ? "Seed" : "Deploy")} requires an exact Municode release tag.");
        SeedFiles.CreateDirectory(directory);
        var version = tag["municode-v".Length..];
        var bundle = $"municode-{version}-linux-any.tar.gz";
        var names = new[] { "release-manifest.json", bundle, "checksums.txt", "checksums.txt.sigstore.json" };
        foreach (var name in names)
            await DownloadAsync(tag, name, Path.Combine(directory, name), cancellationToken);

        var versionResult = await _process.RunAsync("cosign", ["version", "--json"], null, cancellationToken);
        SeedGuard.Require(versionResult.ExitCode == 0
            && versionResult.Output.Contains("v2.6.5", StringComparison.Ordinal),
            "Seed requires independently installed Cosign 2.6.5 before release verification.");
        var verification = await _process.RunAsync("cosign",
        [
            "verify-blob", "--new-bundle-format", "--bundle",
            Path.Combine(directory, "checksums.txt.sigstore.json"),
            "--certificate-identity",
            $"https://github.com/muniperez/municode/.github/workflows/municode-release.yml@refs/tags/{tag}",
            "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
            Path.Combine(directory, "checksums.txt")
        ], null, cancellationToken);
        SeedGuard.Require(verification.ExitCode == 0,
            "The release inventory does not verify against the exact Municode GitHub Actions signing identity.");
        return VerifyFiles(tag, directory, deployProtocol);
    }

    public Task<SeedVerifiedRelease> VerifyLocalAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(directory);
        var local = VerifyLocalBuild(root, DeployReleaseSource.Protocol);
        if (CliSourceRevision() is { Length: 40 } cliRevision)
            SeedGuard.Require(cliRevision == local.SourceRevision,
                "This local build's CLI and server payloads do not come from the same source revision.");
        var identity = SeedReleaseIdentity.Validate(new(
            local.Tag, "municode", "https://github.com/muniperez/municode", local.SourceRevision,
            local.BundleName, local.BundleSha256, local.ManifestSha256, DeployReleaseSource.LocalBuild), local.Tag);
        return Task.FromResult(new SeedVerifiedRelease(identity, root));
    }

    internal static bool IsReleaseTag(string tag) => Regex.IsMatch(tag,
        @"\Amunicode-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z");

    internal static SeedVerifiedRelease VerifyFiles(string tag, string directory, int? deployProtocol = null)
    {
        var version = tag["municode-v".Length..];
        var bundle = $"municode-{version}-linux-any.tar.gz";
        VerifyInventory(directory, new[] { "release-manifest.json", bundle });
        var manifestPath = Path.Combine(directory, "release-manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        SeedGuard.Require(manifest.GetProperty("schemaVersion").GetInt32() == 1
            && manifest.GetProperty("product").GetString() == "municode"
            && manifest.GetProperty("version").GetString() == version
            && manifest.GetProperty("rid").GetString() == "linux-any"
            && manifest.GetProperty("sourceRepository").GetString() == "https://github.com/muniperez/municode"
            && manifest.GetProperty("bundle").GetString() == bundle,
            "The signed release manifest has unexpected standalone product identity, version, RID or bundle.");
        var revision = manifest.GetProperty("sourceRevision").GetString();
        SeedGuard.Require(Regex.IsMatch(revision ?? "", "^[0-9a-f]{40}$"),
            "The signed release source revision is not an exact Git object ID.");
        if (deployProtocol is { } protocol)
        {
            RequireDeployProtocol(manifest, protocol);
            SeedGuard.Require(!manifest.TryGetProperty("imageArchives", out _),
                "A signed deploy release must not include a local image archive inventory.");
        }
        else if (CliSourceRevision() is { Length: 40 } cliRevision)
            SeedGuard.Require(cliRevision == revision,
                "This release's CLI and server payloads do not come from the same source revision.");
        VerifyDependencies(manifest);
        VerifyComponentsAndArchive(directory, manifest, bundle, deploy: deployProtocol is not null);
        var identity = SeedReleaseIdentity.Validate(new(
            tag,
            manifest.GetProperty("product").GetString()!,
            manifest.GetProperty("sourceRepository").GetString()!,
            revision!,
            bundle,
            FileDigest(Path.Combine(directory, bundle)),
            FileDigest(manifestPath),
            "github-sigstore"), tag);
        return new(identity, directory);
    }

    internal static LocalBuildRelease VerifyLocalBuild(string directory, int protocol)
    {
        var path = Path.Combine(directory, "release-manifest.json");
        CheckLocalAsset(path, 128L * 1024 * 1024);
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var manifest = document.RootElement;
        RequireDeployProtocol(manifest, protocol);
        var version = manifest.GetProperty("version").GetString();
        var revision = manifest.GetProperty("sourceRevision").GetString();
        var tag = "municode-v" + version;
        var bundle = $"municode-{version}-linux-any.tar.gz";
        SeedGuard.Require(IsReleaseTag(tag)
            && manifest.GetProperty("schemaVersion").GetInt32() == 1
            && manifest.GetProperty("product").GetString() == "municode"
            && manifest.GetProperty("rid").GetString() == "linux-any"
            && manifest.GetProperty("sourceRepository").GetString() == "https://github.com/muniperez/municode"
            && manifest.GetProperty("bundle").GetString() == bundle
            && revision is not null && Regex.IsMatch(revision, @"\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z"),
            "The local build manifest has an invalid release identity or inexact source revision.");
        SeedGuard.Require(manifest.TryGetProperty("sourceDirty", out var dirty) && dirty.ValueKind == JsonValueKind.False,
            "A clean local build is required (sourceDirty: false).");
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
        var inventory = VerifyInventory(directory, ["release-manifest.json", bundle], local: true);
        SeedGuard.Require(Hash(bytes) == inventory["release-manifest.json"],
            "The local build manifest changed during verification.");
        VerifyDependencies(manifest);
        VerifyComponentsAndArchive(directory, manifest, bundle, deploy: true, digests,
            inventory["release-manifest.json"]);
        var sha = inventory[bundle];
        return new($"local-{revision![..12]}-{sha[..12]}", tag, revision, bundle, sha,
            inventory["release-manifest.json"]);
    }

    internal static Dictionary<string, string> VerifyInventory(
        string directory, IReadOnlyCollection<string> expected, bool local = false)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var checksums = Path.Combine(directory, "checksums.txt");
        if (local) CheckLocalAsset(checksums, 4096);
        foreach (var line in File.ReadLines(checksums))
        {
            var match = Regex.Match(line, @"^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$");
            SeedGuard.Require(match.Success
                && expected.Contains(match.Groups[2].Value, StringComparer.Ordinal)
                && seen.TryAdd(match.Groups[2].Value, match.Groups[1].Value),
                "The release checksum inventory is duplicate, unsafe or contains an unexpected asset.");
            var path = Path.Combine(directory, match.Groups[2].Value);
            if (local) CheckLocalAsset(path, path.EndsWith(".tar.gz", StringComparison.Ordinal)
                ? 32L * 1024 * 1024 * 1024 : 128L * 1024 * 1024);
            else SeedFiles.Check(path);
            SeedGuard.Require(FileDigest(path) == match.Groups[1].Value,
                "A release asset checksum does not match.");
        }
        SeedGuard.Require(seen.Count == expected.Count, "The release checksum inventory is incomplete.");
        return seen;
    }

    internal static void CheckLocalAsset(string path, long limit)
    {
        var file = new FileInfo(path);
        SeedGuard.Require(file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
            && file.Length > 0 && file.Length <= limit, "A local release asset is missing or unsafe.");
    }

    internal static void RequireDeployProtocol(JsonElement manifest, int protocol) =>
        SeedGuard.Require(manifest.TryGetProperty("deployProtocol", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == protocol,
            "Deploy protocol mismatch; use the CLI from the target release (cli/<rid>/).");

    internal static void VerifyDependencies(JsonElement manifest)
    {
        var images = manifest.GetProperty("dependencies").EnumerateObject().ToArray();
        SeedGuard.Require(images.Length > 0, "The release dependency inventory is empty.");
        foreach (var image in images)
            SeedGuard.Require(Regex.IsMatch(image.Value.GetString() ?? "",
                    @"^[^ \t\r\n]+@sha256:(?!0{64}$)[0-9a-f]{64}$"),
                "A release dependency is not pinned by an immutable image digest.");
    }

    internal static void VerifyComponentsAndArchive(
        string directory,
        JsonElement manifest,
        string bundle,
        bool deploy,
        IReadOnlyDictionary<string, string>? imageDigests = null,
        string? manifestDigest = null)
    {
        const string suffix = "-linux-any.tar.gz";
        SeedGuard.Require(bundle.EndsWith(suffix, StringComparison.Ordinal),
            "The signed bundle has an unexpected archive suffix.");
        var expectedRoot = bundle[..^suffix.Length];
        var components = manifest.GetProperty("components").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value);
        SeedGuard.Require(components.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            new[] { "api", "worker", "gateway", "dashboard", "cli" }),
            "The signed release component inventory is incomplete.");
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, component) in components)
        {
            var prefix = name switch
            {
                "api" => "runtime/api",
                "worker" => "runtime/worker",
                "gateway" => "runtime/gateway",
                "dashboard" => "dashboard",
                "cli" => "cli",
                _ => throw new InvalidOperationException()
            };
            var files = component.GetProperty("files").EnumerateArray().ToArray();
            SeedGuard.Require(files.Length > 0, $"The {name} release component is empty.");
            if (deploy)
                SeedGuard.Require(Hash(Encoding.UTF8.GetBytes(string.Concat(files.Select(file =>
                    $"{file.GetProperty("sha256").GetString()}  {file.GetProperty("path").GetString()}\n"))))
                    == component.GetProperty("sha256").GetString(), "A release component inventory digest does not match.");
            foreach (var file in files)
            {
                var path = expectedRoot + "/" + prefix + "/" + file.GetProperty("path").GetString();
                SeedGuard.Require(SafeMember(path) && expectedFiles.TryAdd(
                        path, file.GetProperty("sha256").GetString()!),
                    "The signed release component inventory contains a duplicate or unsafe path.");
            }
        }
        if (imageDigests is not null)
            foreach (var (path, digest) in imageDigests) expectedFiles.Add(expectedRoot + "/" + path, digest);
        if (deploy)
        {
            var agent = manifest.GetProperty("hostAgent");
            SeedGuard.Require(agent.GetProperty("path").GetString() == "deploy_host.py",
                "The release host agent has an unexpected path.");
            expectedFiles.Add(expectedRoot + "/deploy_host.py", agent.GetProperty("sha256").GetString()!);
        }
        var archive = Path.Combine(directory, bundle);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var verifiedFiles = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        var expectedManifest = manifestDigest ?? FileDigest(Path.Combine(directory, "release-manifest.json"));
        var manifestVerified = false;
        using var raw = File.OpenRead(archive);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            SeedGuard.Require(entry.EntryType is TarEntryType.RegularFile or TarEntryType.Directory
                or TarEntryType.V7RegularFile, "The release archive contains a link or special file.");
            var name = entry.EntryType == TarEntryType.Directory
                && entry.Name.EndsWith("/", StringComparison.Ordinal)
                ? entry.Name[..^1]
                : entry.Name;
            SeedGuard.Require(SafeMember(name) && seen.Add(name),
                "The release archive contains an unsafe or duplicate member.");
            SeedGuard.Require(name.StartsWith(expectedRoot + "/", StringComparison.Ordinal)
                || (entry.EntryType == TarEntryType.Directory && name == expectedRoot),
                "The release archive contains a member outside its exact root.");
            expanded += entry.Length;
            var relative = name.Length > expectedRoot.Length ? name[(expectedRoot.Length + 1)..] : "";
            var image = imageDigests?.ContainsKey(relative) is true;
            SeedGuard.Require(entry.Length <= (image ? 16L * 1024 : 128) * 1024 * 1024
                && expanded <= (imageDigests is null ? 2L : 32L) * 1024 * 1024 * 1024,
                "The release archive exceeds its member or expanded-size safety bound.");
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (name == expectedRoot + "/release-manifest.json")
                manifestVerified = StreamDigest(entry.DataStream ?? Stream.Null) == expectedManifest;
            if (expectedFiles.TryGetValue(name, out var digest))
            {
                SeedGuard.Require(StreamDigest(entry.DataStream ?? Stream.Null) == digest,
                    "An archive component digest does not match the external manifest.");
                verifiedFiles.Add(name);
            }
        }
        SeedGuard.Require(expectedFiles.Keys.All(verifiedFiles.Contains)
            && manifestVerified,
            "The release archive is missing signed components or carries a different manifest.");
    }

    private async Task DownloadAsync(
        string tag,
        string name,
        string target,
        CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            SeedFiles.Check(target);
            return;
        }
        var partial = target + ".partial";
        SeedGuard.Require(!File.Exists(partial),
            "A partial release download remains. Reconcile or explicitly remove it after preserving the journal.");
        using var http = new HttpClient(_handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        }) { Timeout = TimeSpan.FromMinutes(10) };
        var uri = new Uri($"https://github.com/muniperez/municode/releases/download/{tag}/{name}");
        using var output = SeedFiles.CreateNew(partial);
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            SeedGuard.Require(uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0,
                "Release redirects must remain public HTTPS.");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            SeedGuard.Require(response.IsSuccessStatusCode,
                $"Exact Municode release asset '{name}' is unavailable. No VPS is created by release verification.");
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[65536];
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                size += read;
                SeedGuard.Require(size <= 2L * 1024 * 1024 * 1024,
                    "A release asset exceeds the seed safety bound.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            output.Flush(true);
            output.Dispose();
            File.Move(partial, target);
            return;
        }
        throw new CliCommandException(CliExitCodes.ValidationError,
            "Too many release redirects; no provider mutation was attempted.");
    }

    internal static bool SafeMember(string name) =>
        !string.IsNullOrEmpty(name) && !name.Contains('\\') && !name.Contains(':')
        && name.Split('/').All(part => part is not ("" or "." or "..")
            && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));

    internal static string CliSourceRevision()
    {
        var value = typeof(CliApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var match = Regex.Match(value, @"(?:sha\.|\+)([0-9a-f]{40})(?:$|\.)");
        return match.Success ? match.Groups[1].Value : "";
    }

    internal static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return StreamDigest(stream);
    }

    private static string StreamDigest(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    internal static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
