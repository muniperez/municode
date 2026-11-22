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
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
            process.StandardInput.Close();
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
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
