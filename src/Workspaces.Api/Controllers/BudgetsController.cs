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
    BudgetService budgets, ProviderChargeService charges, AuditEventService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> List(string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadBudgets, ct)) return EndpointResults.Forbidden();
        return Results.Ok(await charges.AttachAsync(organizationId, await budgets.ListAsync(organizationId, ct), ct));
    }

    [HttpPost]
    public async Task<IResult> Create(CreateBudgetRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManageBudgets, ct)) return EndpointResults.Forbidden();
        return await RunAsync(async () =>
        {
            BudgetChange change;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                change = await budgets.CreateAsync(request, Actor(), ct);
                await SaveAuditAsync(change.Budget, "budget.created", ct);
                await tx.CommitAsync(ct);
            }
            return Results.Created($"/api/budgets/{change.Budget.Id}",
                await charges.AttachAsync(await budgets.ProjectAsync(change.Budget, change.WebhookSigningSecret, ct), ct));
        });
    }

    [HttpPut("{id}")]
    public async Task<IResult> Update(string id, UpdateBudgetRequest request, CancellationToken ct) =>
        await MutateAsync(id, async budget =>
        {
            var change = await budgets.UpdateAsync(budget, request, ct);
            await SaveAuditAsync(budget, "budget.updated", ct);
            return change;
        }, async change => Results.Ok(await charges.AttachAsync(
            await budgets.ProjectAsync(change.Budget, change.WebhookSigningSecret, ct), ct)), ct);

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct) =>
        await MutateAsync(id, async budget =>
        {
            var retired = await budgets.DeleteAsync(budget, ct);
            await SaveAuditAsync(budget, "budget.deleted", ct);
            return new BudgetChange(budget, null, retired);
        }, _ => Task.FromResult(Results.NoContent()), ct);

    private async Task<IResult> MutateAsync(string id, Func<Budget, Task<BudgetChange>> action,
        Func<BudgetChange, Task<IResult>> respond, CancellationToken ct)
    {
        var org = await db.Budgets.Where(x => x.Id == id).Select(x => x.OrganizationId).SingleOrDefaultAsync(ct);
        if (org is null) return EndpointResults.NotFound("budget_not_found", "Budget was not found.");
        if (!await CanAsync(org, OrganizationAction.ManageBudgets, ct)) return EndpointResults.Forbidden();
        return await RunAsync(async () =>
        {
            BudgetChange change;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var budget = await db.Budgets.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct)
                    ?? throw new BudgetException("budget_not_found", "Budget was not found.", 404);
                change = await action(budget);
                await tx.CommitAsync(ct);
            }
            // Old signing secrets are removed only once nothing committed references them.
            await budgets.ForgetSecretAsync(change.RetiredWebhookSecret, CancellationToken.None);
            return await respond(change);
        });
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (BudgetException ex) { return EndpointResults.Problem(ex.Code, ex.Message, ex.Status); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("secret_store_unavailable", "The webhook signing secret could not be stored. Try again later.", 503); }
        catch (DbUpdateConcurrencyException) { return EndpointResults.Conflict("revision_conflict", "This budget changed. Reload it before saving."); }
        catch (DbUpdateException) { return EndpointResults.Conflict("budget_name_taken", "A budget with this name already exists."); }
    }

    private string Actor() => AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity());

    private Task<bool> CanAsync(string org, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), org, action, ct);

    private Task SaveAuditAsync(Budget budget, string action, CancellationToken ct) =>
        audit.AppendAsync(budget.OrganizationId, Actor(), action, JsonSerializer.Serialize(new
        {
            budgetId = budget.Id, budget.Name, budget.Currency, budget.MonthlyAmount, budget.Revision,
            inAppAlerts = budget.InAppAlertsEnabled, webhook = budget.WebhookEnabled, email = budget.EmailEnabled,
            emailAudience = budget.EmailAudience,
            webhookTarget = budget.WebhookUrl is null ? null : new Uri(budget.WebhookUrl).GetLeftPart(UriPartial.Authority)
        }), ct);
}
