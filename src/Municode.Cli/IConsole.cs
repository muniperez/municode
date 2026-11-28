// SPDX-License-Identifier: MPL-2.0

namespace Municode.Cli;

public interface IConsole
{
    bool SupportsAnsi { get; }
    bool IsInteractive => false;
    /// <summary>Standard output is a terminal rather than a file or pipe that might be logged.</summary>
    bool IsOutputTerminal => false;
    string? ReadLine() => null;
    void Write(string message);
    void WriteLine(string message = "");
    void WriteError(string message);
}

public sealed class SystemConsole : IConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsErrorRedirected;
    public bool IsOutputTerminal => !Console.IsOutputRedirected;
    public string? ReadLine() => Console.ReadLine();

    public bool SupportsAnsi =>
        !Console.IsOutputRedirected && !OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TERM") is { Length: > 0 } term &&
        !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);

    public void Write(string message) => Console.Write(message);
    public void WriteLine(string message = "") => Console.WriteLine(message);
    public void WriteError(string message) => Console.Error.WriteLine(message);
}
