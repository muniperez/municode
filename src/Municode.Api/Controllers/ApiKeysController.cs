using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Municode.Api.Auth;
using Municode.Common.Data;
using Municode.Common.Contracts;
using Municode.Common.Models;
using Municode.Api.Providers;
using Municode.Api.Services;
using Municode.Common.Services;
using Municode.Common.Validation;
using static Municode.Api.Controllers.EndpointResults;

namespace Municode.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class ApiKeysController : ControllerBase
{
    [HttpGet]
    public async Task<IResult> ListApiKeys([FromQuery] string organizationId, [FromServices] WorkspaceDbContext db, [FromServices] ControlPlaneAuthorization authorization, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadApiKeys, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        var keys = await db.ApiKeys
            .Where(x => x.OrganizationId == organizationId)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: true, take: null, cancellationToken);
        return Results.Ok(keys.Select(x => x.ToResponse()));
    }

    [HttpPost]
    public async Task<IResult> CreateApiKey([FromBody] CreateApiKeyRequest request, [FromServices] WorkspaceDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] IIdGenerator ids, [FromServices] IApiKeyTokenService tokenService, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var validation = ControlPlaneValidation.ValidateApiKey(request);
        if (!validation.IsValid)
        {
            return EndpointResults.ValidationProblem("API-key request is invalid.", validation);
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), request.OrganizationId, OrganizationAction.WriteApiKeys, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        if (principal.IsApiKey && !request.Scopes.All(principal.Scopes.Contains))
            return EndpointResults.Forbidden();

        Response.Headers.CacheControl = "no-store";
        var token = tokenService.Generate();
        var now = timeProvider.GetUtcNow();
        var key = new ApiKey
        {
            Id = ids.NewId("key"),
            OrganizationId = request.OrganizationId,
            Name = request.Name,
            KeyPrefix = token.Prefix,
            KeyHash = tokenService.Hash(token.Token),
            ScopesJson = JsonSerializer.Serialize(request.Scopes.Distinct(StringComparer.Ordinal).Order().ToArray()),
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/api-keys/{key.Id}", new ApiKeyCreateResponse(key.Id, key.Name, key.KeyPrefix, token.Token, request.Scopes, key.CreatedAt, key.ExpiresAt));
    }

    [HttpDelete("{apiKeyId}")]
    public async Task<IResult> RevokeApiKey([FromRoute] string apiKeyId, [FromServices] WorkspaceDbContext db, [FromServices] ControlPlaneAuthorization authorization, [FromServices] TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == apiKeyId, cancellationToken);
        if (key is null)
        {
            return EndpointResults.NotFound("api_key_not_found", "API key was not found.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), key.OrganizationId, OrganizationAction.WriteApiKeys, cancellationToken))
        {
            return EndpointResults.Forbidden();
        }

        key.RevokedAt ??= timeProvider.GetUtcNow();
        key.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}
