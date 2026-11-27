using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Municode.Common.Data;

namespace Municode.Api.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet("ready")]
    public async Task<IResult> Ready([FromServices] WorkspaceDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            return Results.Problem("Database is not reachable.", statusCode: 503);

        var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        return pendingMigrations.Any()
            ? Results.Problem("Workspaces database migrations are pending.", statusCode: 503)
            : Results.Ok(new { status = "ready" });
    }
}
