// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class SeedProcessTests
{
    [Fact]
    public void ResolveToolUsesFullyQualifiedPathEntriesOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = SeedTestDirectory.Create("seed-process");
        try
        {
            var untrusted = CreateDirectory(root, "untrusted");
            var notExecutable = CreateDirectory(root, "not-executable");
            var trusted = CreateDirectory(root, "trusted");
            WriteTool(untrusted, executable: true);
            WriteTool(notExecutable, executable: false);
            Directory.CreateDirectory(Path.Combine(root, "directory-entry", "fake-tool"));
            var trustedTool = WriteTool(trusted, executable: true);
            var relativeUntrusted = Path.GetRelativePath(Environment.CurrentDirectory, untrusted);
            var searchPath = string.Join(Path.PathSeparator,
                "", ".", relativeUntrusted, notExecutable, Path.Combine(root, "directory-entry"), trusted);

            Assert.Equal(trustedTool, SeedProcess.ResolveTool("fake-tool", searchPath));
            Assert.Null(SeedProcess.ResolveTool("fake-tool", string.Join(Path.PathSeparator, relativeUntrusted, notExecutable)));
            Assert.Equal("/bin/sh", SeedProcess.ResolveTool("/bin/sh", ""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingToolReportsUnavailable()
    {
        var name = "municode-missing-tool-" + Guid.NewGuid().ToString("N");

        var error = await Assert.ThrowsAsync<CliCommandException>(() =>
            SeedProcess.Default.RunAsync(name, [], null, CancellationToken.None));

        Assert.Equal($"Required seed tool is unavailable: {name}.", error.Message);
    }

    private static string CreateDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteTool(string directory, bool executable)
    {
        var path = Path.Combine(directory, "fake-tool");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}
