using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspaces.Api.Services;
using Workspaces.Api.Auth;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Models;
using Workspaces.Common.Providers;
using Workspaces.Common.Providers.Common;
using Workspaces.Common.Services;

namespace Workspaces.Api.Controllers;

[ApiController, Authorize, Route("api/providers")]
public sealed class ProvidersController(WorkspaceDbContext db, ControlPlaneAuthorization authorization,
    ProviderAccountService accounts, AuditEventService audit) : ControllerBase
{
    [HttpGet("/api/provider-types")]
    public IResult Types() => Results.Ok(new[]
    {
        new ProviderTypeResponse("hetzner", "Hetzner", true, "ubuntu-24.04"),
        new ProviderTypeResponse("digital-ocean", "DigitalOcean", true, "ubuntu-24-04-x64"),
        new ProviderTypeResponse("vultr", "Vultr", true, null, 2284)
    });

    [HttpGet]
    public async Task<IResult> List(string organizationId, CancellationToken ct)
    {
        if (!await CanAsync(organizationId, OrganizationAction.ReadProviders, ct)) return EndpointResults.Forbidden();
        return Results.Ok((await db.ProviderAccounts.AsNoTracking().Include(x => x.Plans)
            .Where(x => x.OrganizationId == organizationId).OrderBy(x => x.Name).ToListAsync(ct)).Select(ToResponse));
    }

    [HttpGet("permissions")]
    public async Task<IResult> Permissions(string organizationId, CancellationToken ct) =>
        Results.Ok(new ProviderPermissionsResponse(
            await CanAsync(organizationId, OrganizationAction.ReadProviders, ct),
            await CanAsync(organizationId, OrganizationAction.ManageProviders, ct)));

    [HttpGet("{id}")]
    public async Task<IResult> Get(string id, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.AsNoTracking().Include(x => x.Plans).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return EndpointResults.NotFound("provider_not_found", "Provider account was not found.");
        if (!await CanAsync(account.OrganizationId, OrganizationAction.ReadProviders, ct)) return EndpointResults.Forbidden();
        return Results.Ok(ToResponse(account));
    }

    [HttpPost]
    public async Task<IResult> Create(CreateProviderRequest request, CancellationToken ct)
    {
        if (!await CanAsync(request.OrganizationId, OrganizationAction.ManageProviders, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(async () =>
        {
            var account = await accounts.CreateAsync(request, ct);
            await SaveAuditAsync(account, "provider.created", ct);
            return Results.Created($"/api/providers/{account.Id}", ToResponse(account));
        });
    }

    [HttpPatch("{id}")]
    public async Task<IResult> Update(string id, UpdateProviderRequest request, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            accounts.Update(account, request);
            await SaveAuditAsync(account, "provider.updated", ct);
            return Results.Ok(ToResponse(account));
        }, ct);

    [HttpPut("{id}/credentials")]
    public async Task<IResult> Credentials(string id, ReplaceProviderCredentialRequest request, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            await accounts.ReplaceCredentialsAsync(account, request, ct);
            await SaveAuditAsync(account, "provider.credentials_replaced", ct);
            return Results.Ok(ToResponse(account));
        }, ct);

    [HttpPost("{id}/validate")]
    public async Task<IResult> Validate(string id, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            var result = await accounts.ValidateAsync(account, ct);
            await SaveAuditAsync(account, "provider.validated", ct);
            return Results.Ok(result);
        }, ct);

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct) =>
        await MutateAsync(id, async account =>
        {
            await accounts.DeleteAsync(account, ct);
            await SaveAuditAsync(account, "provider.deleted", ct);
            return Results.NoContent();
        }, ct);

    private async Task<IResult> MutateAsync(string id, Func<ProviderAccount, Task<IResult>> action, CancellationToken ct)
    {
        var account = await db.ProviderAccounts.Include(x => x.Plans).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (account is null) return EndpointResults.NotFound("provider_not_found", "Provider account was not found.");
        if (!await CanAsync(account.OrganizationId, OrganizationAction.ManageProviders, ct)) return EndpointResults.Forbidden();
        return await ProviderEndpointResults.RunAsync(() => action(account));
    }

    private Task<bool> CanAsync(string organizationId, OrganizationAction action, CancellationToken ct) =>
        authorization.CanAccessOrganizationAsync(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity(), organizationId, action, ct);

    private Task SaveAuditAsync(ProviderAccount account, string action, CancellationToken ct) =>
        audit.AppendAsync(account.OrganizationId, AuditEventService.ActorFor(EndpointResults.RequirePrincipal(HttpContext).ToActorIdentity()),
            action, System.Text.Json.JsonSerializer.Serialize(new { providerAccountId = account.Id, account.Revision }), ct);

    public static ProviderAccountResponse ToResponse(ProviderAccount account) => new(account.Id, account.OrganizationId,
        account.Name, ProviderAccountService.WireType(account.ProviderType), account.Enabled, account.ProjectId, account.DefaultOsId,
        account.ValidationStatus, account.LastValidatedAt, account.Revision, account.Plans.Count);
}

internal static class ProviderEndpointResults
{
    public static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ProviderCatalogException ex)
        {
            var status = ex.Code is "revision_conflict" or "plan_name_taken" or "plan_mapping_locked" or "provider_in_use" or "plan_in_use"
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return EndpointResults.Problem(ex.Code, ex.Message,
                ex.Code == "plan_not_found" ? StatusCodes.Status404NotFound : status);
        }
        catch (DbUpdateConcurrencyException) { return EndpointResults.Conflict("revision_conflict", "This item changed. Reload it before saving."); }
        catch (DbUpdateException) { return EndpointResults.Conflict("catalog_conflict", "This change conflicts with another catalog update or an existing resource reference."); }
        catch (SecretStoreUnavailableException) { return EndpointResults.Problem("provider_credentials_unavailable", "Provider credential storage is unavailable. Retry when OpenBao is available.", 503); }
        catch (HttpRequestException) { return EndpointResults.Problem("provider_validation_failed", "Provider credentials or access could not be validated. Check the token and cloud account permissions.", 400); }
        catch (System.Text.Json.JsonException) { return EndpointResults.Problem("provider_validation_failed", "The provider returned an invalid response.", 502); }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
        { return EndpointResults.Problem("provider_unavailable", "The provider did not respond in time.", 503); }
    }
}
