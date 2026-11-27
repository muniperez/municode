// SPDX-License-Identifier: MPL-2.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class SeedReleaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactSignedInventoryAndArchiveIdentityAreVerified(bool includeDirectories)
    {
        using var fixture = new ReleaseFixture(includeDirectories: includeDirectories);
        var verified = SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root);
        Assert.Equal(ReleaseFixture.Revision(), verified.Identity.SourceRevision);
        Assert.Equal("github-sigstore", verified.Identity.Trust);
    }

    [Fact]
    public void ComponentMismatchFailsClosedEvenWithMatchingExternalChecksums()
    {
        using var fixture = new ReleaseFixture(tamperComponent: true);
        var error = Assert.Throws<CliCommandException>(
            () => SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root));
        Assert.Contains("component digest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("municode-workspaces-1.2.3//", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime/api//", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime//extra/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/../extra/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/./extra/", TarEntryType.Directory)]
    [InlineData("/municode-workspaces-1.2.3/extra/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime\\extra/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/C:extra/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/extra.txt/", TarEntryType.RegularFile)]
    [InlineData("municode-workspaces-1.2.3/extra.txt/", TarEntryType.V7RegularFile)]
    [InlineData("municode-workspaces-1.2.3/runtime/api", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime/api/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime/api/Municode.Api.dll/", TarEntryType.Directory)]
    [InlineData("municode-workspaces-1.2.3/runtime/api/Municode.Api.dll", TarEntryType.RegularFile)]
    public void UnsafeOrDuplicateArchiveMembersFailClosed(string name, TarEntryType type)
    {
        using var fixture = new ReleaseFixture(extraMember: name, extraType: type);
        var error = Assert.Throws<CliCommandException>(
            () => SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root));
        Assert.Contains("unsafe or duplicate member", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("other-root/")]
    [InlineData("municode-workspaces-1.2.30/extra/")]
    [InlineData("municode-workspaces-1.2.3-extra/")]
    public void DirectoriesOutsideTheExactArchiveRootFailClosed(string name)
    {
        using var fixture = new ReleaseFixture(extraMember: name);
        var error = Assert.Throws<CliCommandException>(
            () => SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root));
        Assert.Contains("outside its exact root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectoryCannotSubstituteForSignedComponentFile()
    {
        using var fixture = new ReleaseFixture(omitApi: true,
            extraMember: "municode-workspaces-1.2.3/runtime/api/Municode.Api.dll/");
        var error = Assert.Throws<CliCommandException>(
            () => SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root));
        Assert.Contains("missing signed components", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeedRefusesUnsignedOrUnexpectedReleaseInventory()
    {
        using var fixture = new ReleaseFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "checksums.txt"),
            new string('0', 64) + "  release-manifest.json\n");
        ReleaseFixture.Protect(Path.Combine(fixture.Root, "checksums.txt"));
        Assert.Throws<CliCommandException>(
            () => SeedRelease.VerifyFiles("workspaces-v1.2.3", fixture.Root));
    }

    private sealed class ReleaseFixture : IDisposable
    {
        public ReleaseFixture(
            bool tamperComponent = false,
            bool includeDirectories = true,
            string? extraMember = null,
            TarEntryType extraType = TarEntryType.Directory,
            bool omitApi = false)
        {
            Root = SeedTestDirectory.Create("release");
            Build(tamperComponent, includeDirectories, extraMember, extraType, omitApi);
        }

        public string Root { get; }

        private void Build(
            bool tamperComponent,
            bool includeDirectories,
            string? extraMember,
            TarEntryType extraType,
            bool omitApi)
        {
            var version = "1.2.3";
            var archiveRoot = "municode-workspaces-" + version;
            var componentBytes = new Dictionary<string, byte[]>
            {
                ["api/Municode.Api.dll"] = "api"u8.ToArray(),
                ["worker/Municode.Worker.dll"] = "worker"u8.ToArray(),
                ["gateway/Municode.Gateway.dll"] = "gateway"u8.ToArray(),
                ["dashboard/index.html"] = "<html />"u8.ToArray(),
                ["cli/linux-x64/municode"] = "cli"u8.ToArray()
            };
            var components = componentBytes.Keys
                .GroupBy(key => key.Split('/')[0])
                .ToDictionary(group => group.Key, group =>
                {
                    var files = group.Select(path =>
                    {
                        var relative = path[(path.IndexOf('/') + 1)..];
                        var digest = Hash(componentBytes[path]);
                        return new
                        {
                            path = relative,
                            sha256 = path.StartsWith("api/", StringComparison.Ordinal) && tamperComponent
                                ? new string('f', 64)
                                : digest,
                            size = componentBytes[path].Length,
                            executable = path.Contains("cli/", StringComparison.Ordinal)
                        };
                    }).ToArray();
                    return new
                    {
                        files,
                        sha256 = Hash(Encoding.UTF8.GetBytes(string.Join("\n",
                            files.Select(file => file.sha256 + "  " + file.path))))
                    };
                });
            components = components.ToDictionary(item => item.Key, item => item.Value,
                StringComparer.Ordinal);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                product = "municode-workspaces",
                version,
                rid = "linux-any",
                sourceRepository = "https://github.com/muniperez/municode",
                sourceRevision = Revision(),
                targets = new { },
                dependencies = new
                {
                    dotnet = "mcr.microsoft.com/dotnet/aspnet@sha256:" + new string('1', 64)
                },
                components,
                bundle = $"municode-workspaces-{version}-linux-any.tar.gz"
            }, new JsonSerializerOptions { WriteIndented = true });
            var manifestPath = Path.Combine(Root, "release-manifest.json");
            File.WriteAllBytes(manifestPath, manifest);
            Protect(manifestPath);

            var archivePath = Path.Combine(Root, $"municode-workspaces-{version}-linux-any.tar.gz");
            using (var raw = File.Create(archivePath))
            using (var gzip = new GZipStream(raw, CompressionMode.Compress))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                if (includeDirectories)
                {
                    foreach (var path in new[]
                    {
                        "", "runtime", "runtime/api", "runtime/worker", "runtime/gateway",
                        "dashboard", "cli", "cli/linux-x64"
                    })
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory,
                            archiveRoot + (path.Length == 0 ? "" : "/" + path) + "/"));
                }
                Write(tar, archiveRoot, manifest, "release-manifest.json");
                foreach (var (path, bytes) in componentBytes)
                {
                    if (omitApi && path.StartsWith("api/", StringComparison.Ordinal)) continue;
                    var root = path.Split('/')[0] switch
                    {
                        "api" => "runtime/api",
                        "worker" => "runtime/worker",
                        "gateway" => "runtime/gateway",
                        "dashboard" => "dashboard",
                        "cli" => "cli",
                        _ => throw new InvalidOperationException()
                    };
                    Write(tar, archiveRoot, bytes, root + "/" + path[(path.IndexOf('/') + 1)..]);
                }
                if (extraMember is not null)
                {
                    using var data = new MemoryStream("extra"u8.ToArray());
                    TarEntry extra = extraType == TarEntryType.V7RegularFile
                        ? new V7TarEntry(extraType, extraMember)
                        : new PaxTarEntry(extraType, extraMember);
                    if (extraType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        extra.DataStream = data;
                    tar.WriteEntry(extra);
                }
            }
            Protect(archivePath);
            var checksums = Path.Combine(Root, "checksums.txt");
            File.WriteAllText(checksums,
                Hash(File.ReadAllBytes(manifestPath)) + "  release-manifest.json\n"
                + Hash(File.ReadAllBytes(archivePath))
                + $"  municode-workspaces-{version}-linux-any.tar.gz\n");
            Protect(checksums);
        }

        private static void Write(TarWriter tar, string root, byte[] bytes, string path)
        {
            using var data = new MemoryStream(bytes);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, root + "/" + path)
            {
                DataStream = data
            };
            tar.WriteEntry(entry);
        }

        internal static void Protect(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static string Hash(byte[] bytes) =>
            Convert.ToHexStringLower(SHA256.HashData(bytes));

        internal static string Revision() =>
            SeedRelease.CliSourceRevision() is { Length: 40 } value ? value : new string('a', 40);

        public void Dispose() => Directory.Delete(Root, true);
    }
}
