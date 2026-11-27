// SPDX-License-Identifier: MPL-2.0

namespace Municode.Cli.Commands;

public sealed class CliCommandException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
