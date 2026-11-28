// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Text;
using Municode.Cli.Commands;

namespace Municode.Cli.Seed;

internal sealed record SeedProcessResult(int ExitCode, string Output);

internal interface ISeedProcess
{
    Task<SeedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? input,
        CancellationToken cancellationToken);

    Task<SeedProcessResult> RunStreamAsync(
        string fileName, IReadOnlyList<string> arguments, Stream input, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This process runner does not support streamed input.");

    Task<SeedProcessResult> RunDownloadAsync(
        string fileName, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This process runner does not support streamed output.");
}

internal sealed class SeedProcess : ISeedProcess
{
    public static readonly SeedProcess Default = new();

    public async Task<SeedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? input,
        CancellationToken cancellationToken)
    {
        using var stream = input is null ? null : new MemoryStream(input, writable: false);
        return await RunCoreAsync(fileName, arguments, stream, cancellationToken);
    }

    public Task<SeedProcessResult> RunStreamAsync(
        string fileName, IReadOnlyList<string> arguments, Stream input, CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, input, cancellationToken);

    public Task<SeedProcessResult> RunDownloadAsync(
        string fileName, IReadOnlyList<string> arguments, Stream output, CancellationToken cancellationToken) =>
        RunCoreAsync(fileName, arguments, null, cancellationToken, output);

    private static async Task<SeedProcessResult> RunCoreAsync(
        string fileName, IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken,
        Stream? destination = null)
    {
        var resolved = ResolveTool(fileName, Environment.GetEnvironmentVariable("PATH"))
            ?? throw new CliCommandException(CliExitCodes.ValidationError,
                $"Required seed tool is unavailable: {fileName}.");
        var start = new ProcessStartInfo(resolved)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new CliCommandException(CliExitCodes.ValidationError,
                    $"Could not start required seed tool '{fileName}'.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception)
        {
                throw new CliCommandException(CliExitCodes.ValidationError,
                    $"Required seed tool is unavailable: {fileName}.");
        }
        var standardOutput = destination is null ? process.StandardOutput.ReadToEndAsync(cancellationToken) : null;
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
                process.StandardInput.Close();
            }
            if (destination is not null)
                await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        var output = standardOutput is null ? "" : await standardOutput;
        var errorText = await standardError;
        return new(process.ExitCode, (output + errorText).Length <= 4 * 1024 * 1024
            ? output
            : output[..Math.Min(output.Length, 4 * 1024 * 1024)]);
    }

    // Bare names are resolved against PATH only; .NET would otherwise try the executable's
    // directory and the current directory first.
    internal static string? ResolveTool(string fileName, string? searchPath)
    {
        if (fileName.Length == 0) return null;
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
            return fileName;
        if (string.IsNullOrEmpty(searchPath)) return null;
        var candidates = new List<string> { fileName };
        if (OperatingSystem.IsWindows() && !Path.HasExtension(fileName))
        {
            var extensions = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            candidates.InsertRange(0, extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => fileName + extension));
        }
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (IsExecutableFile(path)) return path;
            }
        }
        return null;
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<SeedProcessResult> RequireAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[]? input = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, input, cancellationToken);
        SeedGuard.Require(result.ExitCode == 0,
            $"Required seed tool '{fileName}' rejected the command. Secret-bearing diagnostics are suppressed.");
        return result;
    }
}
