// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

internal static class SeedTestDirectory
{
    public static string Create(string prefix)
    {
        // The test output directory already exists; only the unique fixture root needs mode 0700.
        var root = Path.Combine(AppContext.BaseDirectory, prefix + "-" + Guid.NewGuid().ToString("N"));
        SeedFiles.CreateDirectory(root);
        return root;
    }
}
