using Renci.SshNet;

namespace Workspaces.Worker.DevelopmentWorkspaces;

public sealed class WorkspaceSshOptions
{
    public const string SectionName = "WorkspaceSsh";
    public string PublicKey { get; set; } = "";
    public string PrivateKeyFile { get; set; } = "";
    public string? PrivateKeyPassphrase { get; set; }
    internal PrivateKeyFile ReadPrivateKey() => string.IsNullOrEmpty(PrivateKeyPassphrase)
        ? new PrivateKeyFile(PrivateKeyFile)
        : new PrivateKeyFile(PrivateKeyFile, PrivateKeyPassphrase);

    internal void Validate()
    {
        if (!Path.IsPathFullyQualified(PrivateKeyFile) ||
            (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(PrivateKeyFile) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
            throw new InvalidOperationException("Workspace SSH requires an absolute, owner-only private key file.");
        try
        {
            using var key = ReadPrivateKey();
            var parts = PublicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (PublicKey.Length > 8192 || PublicKey.Any(char.IsControl) || parts.Length < 2 ||
                parts[0] is not ("ssh-ed25519" or "ssh-rsa"))
                throw new InvalidOperationException();
            var encoded = Convert.FromBase64String(parts[1]);
            if (!key.HostKeyAlgorithms.Any(algorithm => encoded.AsSpan().SequenceEqual(algorithm.Data)))
                throw new InvalidOperationException();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidOperationException("Workspace SSH requires a readable private key and its matching OpenSSH public key.");
        }
    }
}
