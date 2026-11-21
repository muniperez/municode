// SPDX-License-Identifier: MPL-2.0

namespace Municode.Cli;

public interface IConsole
{
    bool SupportsAnsi { get; }
    void Write(string message);
    void WriteLine(string message = "");
    void WriteError(string message);
}

public sealed class SystemConsole : IConsole
{
    public bool SupportsAnsi =>
        !Console.IsOutputRedirected && !OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TERM") is { Length: > 0 } term &&
        !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);

    public void Write(string message) => Console.Write(message);
    public void WriteLine(string message = "") => Console.WriteLine(message);
    public void WriteError(string message) => Console.Error.WriteLine(message);
}
