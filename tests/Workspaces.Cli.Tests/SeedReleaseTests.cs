// SPDX-License-Identifier: MPL-2.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Workspaces.Cli.Tests;

public sealed class SeedReleaseTests
{
    [Fact]
    public void ExactSignedInventoryAndArchiveIdentityAreVerified()
    {
        using var fixture = new ReleaseFixture();
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
        public ReleaseFixture(bool tamperComponent = false)
        {
            Root = Path.Combine(Directory.GetCurrentDirectory(), ".local", "artifacts", "seed-tests",
                "release-" + Guid.NewGuid().ToString("N"));
            SeedFiles.CreateDirectory(Root);
            Build(tamperComponent);
        }

        public string Root { get; }

        private void Build(bool tamperComponent)
        {
            var version = "1.2.3";
            var archiveRoot = "municode-workspaces-" + version;
            var componentBytes = new Dictionary<string, byte[]>
            {
                ["api/Workspaces.Api.dll"] = "api"u8.ToArray(),
                ["worker/Workspaces.Worker.dll"] = "worker"u8.ToArray(),
                ["gateway/Workspaces.Gateway.dll"] = "gateway"u8.ToArray(),
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
                Write(tar, archiveRoot, manifest, "release-manifest.json");
                foreach (var (path, bytes) in componentBytes)
                {
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
