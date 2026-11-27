// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.Json;
using Municode.Cli.Seed;

namespace Municode.Cli.Deploy;

internal static class DeployBackup
{
    private const string Manifest = "backup-manifest.json";
    private static readonly string[] Files =
    [
        "active-release.json", "caddy-data.tar", "config.tar", "kratos.dump", "municode.dump",
        "openbao.tar", "postgres-globals.sql", "secrets.tar"
    ];

    public static async Task<string> DownloadAsync(
        DeployTransport transport, string operation, string directory, CancellationToken ct)
    {
        SeedFiles.CreateDirectory(directory);
        var target = Path.Combine(directory, operation);
        var result = await transport.ReadBackupFileAsync(operation, Manifest, ct);
        SeedGuard.Require(result.ExitCode == 0, "Could not read the off-host backup manifest over pinned SSH.");
        using var document = JsonDocument.Parse(result.Output);
        var manifest = document.RootElement;
        var files = manifest.GetProperty("files");
        SeedGuard.Require(manifest.GetProperty("format").GetInt32() == 1
            && manifest.GetProperty("operationId").GetString() == operation
            && files.EnumerateObject().Select(file => file.Name).Order(StringComparer.Ordinal).SequenceEqual(Files),
            "The backup manifest does not match this operation or its expected snapshot files.");
        var hashes = Files.ToDictionary(file => file, file => files.GetProperty(file).GetString(), StringComparer.Ordinal);
        SeedGuard.Require(hashes.Values.All(DeployApproval.IsDigest), "The backup manifest has invalid SHA-256 hashes.");

        if (Path.Exists(target))
        {
            SeedFiles.Check(target, directory: true);
            SeedFiles.Check(Path.Combine(target, Manifest));
            SeedGuard.Require(await File.ReadAllTextAsync(Path.Combine(target, Manifest), ct) == result.Output,
                "The local backup manifest does not match the host; existing files were preserved.");
            foreach (var file in Files) await VerifyAsync(Path.Combine(target, file), hashes[file], ct);
            return target;
        }

        var staging = Path.Combine(directory, "." + operation + "-" + Guid.NewGuid().ToString("N"));
        SeedFiles.CreateDirectory(staging);
        try
        {
            foreach (var file in Files)
            {
                var path = Path.Combine(staging, file);
                await using (var output = SeedFiles.CreateNew(path))
                {
                    var download = await transport.ReadBackupFileAsync(operation, file, ct, output);
                    SeedGuard.Require(download.ExitCode == 0, $"Off-host backup transfer failed for {file}.");
                    output.Flush(flushToDisk: true);
                }
                await VerifyAsync(path, hashes[file], ct);
            }
            SeedFiles.WriteNew(Path.Combine(staging, Manifest), Encoding.UTF8.GetBytes(result.Output));
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in Files.Append(Manifest)) File.Delete(Path.Combine(staging, file));
                Directory.Delete(staging);
            }
        }
    }

    private static async Task VerifyAsync(string path, string? expected, CancellationToken ct)
    {
        SeedFiles.Check(path);
        await using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(input, ct));
        SeedGuard.Require(hash == expected, $"Off-host backup SHA-256 mismatch for {Path.GetFileName(path)}.");
    }
}
