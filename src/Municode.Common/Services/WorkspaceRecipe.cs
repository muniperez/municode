using System.Security.Cryptography;
using System.Text.Json;

namespace Municode.Common.Services;

/// <summary>
/// The pinned workspace tooling recipe shipped with this release. Stock Ubuntu images are provisioned from it
/// over SSH, so their enrollment identity, T3 release and agent list come from this lock, not from the policy.
/// </summary>
public static class WorkspaceRecipe
{
    public const string SnapshotSource = "snapshot";
    public const string UbuntuSource = "ubuntu";

    private static readonly Lazy<(string Sha256, string T3Version, int T3Protocol, DevelopmentWorkspaceAgent[] Agents)> Lock = new(Load);

    public static string Sha256 => Lock.Value.Sha256;
    public static string T3Version => Lock.Value.T3Version;

    public static bool IsStockUbuntu(this DevelopmentWorkspaceImage image) =>
        string.Equals(image.Source, UbuntuSource, StringComparison.Ordinal);

    public static bool HasKnownSource(this DevelopmentWorkspaceImage image) =>
        image.Source is SnapshotSource or UbuntuSource;

    /// <summary>Overwrites recipe-derived fields of stock Ubuntu images; snapshot images are unchanged.</summary>
    public static void Apply(DevelopmentWorkspaceOptions options)
    {
        foreach (var image in options.Images.Values.Where(IsStockUbuntu))
        {
            var recipe = Lock.Value;
            image.ManifestSha256 = recipe.Sha256;
            image.T3Version = recipe.T3Version;
            image.T3EnvironmentProtocol = recipe.T3Protocol;
            image.Agents = recipe.Agents.Select(x => new DevelopmentWorkspaceAgent { Id = x.Id, Name = x.Name, Version = x.Version }).ToArray();
        }
    }

    public static byte[] LockBytes()
    {
        using var stream = typeof(WorkspaceRecipe).Assembly.GetManifestResourceStream("Municode.WorkspaceRecipe.lock.json")
            ?? throw new InvalidOperationException("The pinned workspace recipe is missing from this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static (string, string, int, DevelopmentWorkspaceAgent[]) Load()
    {
        var bytes = LockBytes();
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var t3 = root.GetProperty("t3");
        var agents = root.GetProperty("agents").EnumerateArray().Select(x => new DevelopmentWorkspaceAgent
        {
            Id = x.GetProperty("id").GetString()!, Name = x.GetProperty("name").GetString()!,
            Version = x.GetProperty("version").GetString()!
        }).ToArray();
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), t3.GetProperty("version").GetString()!,
            t3.GetProperty("environmentProtocol").GetInt32(), agents);
    }
}
