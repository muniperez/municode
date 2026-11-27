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
        var start = new ProcessStartInfo(fileName)
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
