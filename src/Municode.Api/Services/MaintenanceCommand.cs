using System.Text.Json;
using Municode.Common.Data;
using Municode.Common.Services;

namespace Municode.Api.Services;

public static class MaintenanceCommand
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, TextWriter error)
    {
        if (args is not ([("enter" or "exit" or "status")] or [("enter" or "exit" or "status"), "--json"]))
        {
            await error.WriteLineAsync("Usage: Municode.Api maintenance enter|exit|status [--json]");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        if (args[0] != "status")
            await WorkspaceMaintenanceMode.SetAsync(db, args[0] == "enter");

        var status = await WorkspaceMaintenanceMode.GetStatusAsync(db);
        await output.WriteLineAsync(args.Length == 2 ? JsonSerializer.Serialize(status, JsonSerializerOptions.Web)
            : $"Workspace maintenance is {(status.Enabled ? "enabled" : "disabled")}. Nonterminal operations: {status.NonterminalOperations}.");
        return 0;
    }
}
