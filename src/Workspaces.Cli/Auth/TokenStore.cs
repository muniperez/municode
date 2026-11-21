// SPDX-License-Identifier: MPL-2.0

using System.Runtime.InteropServices;

namespace Municode.Cli.Auth;

public sealed class TokenStore(CliEnvironment environment)
{
    public string? GetToken()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        if (!File.Exists(environment.TokenFilePath)) return null;
        var token = File.ReadAllText(environment.TokenFilePath).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public void SaveToken(string token)
    {
        var directory = Path.GetDirectoryName(environment.TokenFilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(environment.TokenFilePath, token.Trim() + Environment.NewLine);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(environment.TokenFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
