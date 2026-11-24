using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspaces.Api.Auth;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Services;

namespace Workspaces.Api.Controllers;

// Informational budgets: any member reads, owner/admin manage. Never consulted by workspace lifecycle endpoints.
[ApiController, Authorize, Route("api/budgets")]
public sealed class BudgetsController(WorkspaceDbContext db, ControlPlaneAuthorization authorization,
    BudgetService budgets, AuditEventService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadBudgets, ct)) return EndpointResults.Forbidden();
        return Results.Ok(await budgets.ListAsync(organizationId, ct));
    }

    [HttpPost]
    public async Task<IResult> Create(CreateBudgetRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManageBudgets, ct)) return EndpointResults.Forbidden();
        return await RunAsync(async () =>
        {
            Budget budget;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                budget = await budgets.CreateAsync(request, Actor(), ct);
                await SaveAuditAsync(budget, "budget.created", ct);
                await tx.CommitAsync(ct);
            }
            return Results.Created($"/api/budgets/{budget.Id}", await budgets.ProjectAsync(budget, ct));
        });
    }

    [HttpPut("{id}")]
    public async Task<IResult> Update(string id, UpdateBudgetRequest request, CancellationToken ct) =>
        await MutateAsync(id, async budget =>
        {
            await budgets.UpdateAsync(budget, request, ct);
            await SaveAuditAsync(budget, "budget.updated", ct);
            return budget;
        }, async budget => Results.Ok(await budgets.ProjectAsync(budget, ct)), ct);

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct) =>
        await MutateAsync(id, async budget =>
        {
            await budgets.DeleteAsync(budget, ct);
            await SaveAuditAsync(budget, "budget.deleted", ct);
            return budget;
        }, _ => Task.FromResult(Results.NoContent()), ct);

    private async Task<IResult> MutateAsync(string id, Func<Budget, Task<Budget>> action, Func<Budget, Task<IResult>> respond,
        CancellationToken ct)
    {
        var org = await db.Budgets.Where(x => x.Id == id).Select(x => x.OrganizationId).SingleOrDefaultAsync(ct);
        if (org is null) return EndpointResults.NotFound("budget_not_found", "Budget was not found.");
        if (!await CanAsync(org, OrganizationAction.ManageBudgets, ct)) return EndpointResults.Forbidden();
        return await RunAsync(async () =>
        {
            Budget budget;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                budget = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct)
                    ?? throw new BudgetException("budget_not_found", "Budget was not found.", 404);
                await action(budget);
                await tx.CommitAsync(ct);
            }
            return await respond(budget);
        });
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (BudgetException ex) { return EndpointResults.Problem(ex.Code, ex.Message, ex.Status); }
        catch (DbUpdateConcurrencyException) { return EndpointResults.Conflict("revision_conflict", "This budget changed. Reload it before saving."); }
        catch (DbUpdateException) { return EndpointResults.Conflict("budget_name_taken", "A budget with this name already exists."); }
    }

    private string Actor() => AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity());

    private Task<bool> CanAsync(string org, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), org, action, ct);

    private Task SaveAuditAsync(Budget budget, string action, CancellationToken ct) =>
        audit.AppendAsync(budget.OrganizationId, Actor(), action, JsonSerializer.Serialize(new
        {
            budgetId = budget.Id, budget.Name, budget.Currency, budget.MonthlyAmount, budget.Revision
        }), ct);
}
