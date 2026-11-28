using Municode.Worker.DevelopmentWorkspaces;

namespace Municode.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceSshOptionsTests
{
    [Fact]
    public void WorkerAcceptsOnlyMatchingOwnerOnlyManagementKey()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixture-ssh-" + Guid.NewGuid().ToString("N"));
        var key = WorkspaceBootstrap.Generate();
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var writer = new StreamWriter(new FileStream(path, fileOptions)))
                writer.Write(key.HostPrivateKey);
            var ssh = new WorkspaceSshOptions { PublicKey = key.HostPublicKey, PrivateKeyFile = path };
            ssh.Validate();
            ssh.PublicKey = WorkspaceBootstrap.Generate().HostPublicKey;
            Assert.Throws<InvalidOperationException>(ssh.Validate);
            if (!OperatingSystem.IsWindows())
            {
                ssh.PublicKey = key.HostPublicKey;
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
                Assert.Throws<InvalidOperationException>(ssh.Validate);
            }
        }
        finally { File.Delete(path); }
    }
}
