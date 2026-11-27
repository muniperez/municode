// SPDX-License-Identifier: MPL-2.0

using Municode.Cli;
using Municode.Cli.Api;
using Municode.Cli.Auth;
using Municode.Cli.Commands;

using var cancellation = new CancellationTokenSource();
try
{
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
}
catch (InvalidOperationException) { }

try
{
    var environment = CliEnvironment.FromEnvironment();
    var tokenStore = new TokenStore(environment);
    var client = new MunicodeApiClient(environment, tokenStore);
    return await new CliApplication(new SystemConsole(), environment, tokenStore, client)
        .RunAsync(args, cancellation.Token);
}
catch (CliCommandException error)
{
    Console.Error.WriteLine(error.Message);
    return error.ExitCode;
}
