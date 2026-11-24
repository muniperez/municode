using Microsoft.EntityFrameworkCore;
using Workspaces.Common.Auth;
using Workspaces.Common.Data;
using Workspaces.Common.Models;

namespace Workspaces.Common.Services;

public sealed class ControlPlaneAuthorization(WorkspaceDbContext db)
{
    public async Task<bool> CanAccessOrganizationAsync(ActorIdentity principal, string organizationId, OrganizationAction action, CancellationToken cancellationToken)
    {
        if (principal.IsApiKey)
        {
            return principal.OrganizationId == organizationId && HasScope(principal, ScopeFor(action));
        }

        if (principal.UserId is null)
        {
            return false;
        }

        var role = await db.OrganizationMembers
            .Where(x => x.OrganizationId == organizationId && x.UserId == principal.UserId)
            .Select(x => (OrganizationRole?)x.Role)
            .SingleOrDefaultAsync(cancellationToken);

        return role is not null && RoleAllows(role.Value, action);
    }

    private static bool HasScope(ActorIdentity principal, string? requiredScope) =>
        requiredScope is not null && principal.Scopes.Contains(requiredScope);

    private static string? ScopeFor(OrganizationAction action) => action switch
    {
        OrganizationAction.ReadGitHub => ApiKeyScopes.GitHubRead,
        OrganizationAction.ManageGitHub => ApiKeyScopes.GitHubWrite,
        OrganizationAction.ReadWorkspaces => ApiKeyScopes.WorkspacesRead,
        OrganizationAction.WriteWorkspaces => ApiKeyScopes.WorkspacesWrite,
        OrganizationAction.ReadApiKeys => ApiKeyScopes.ApiKeysRead,
        OrganizationAction.WriteApiKeys => ApiKeyScopes.ApiKeysWrite,
        OrganizationAction.ReadProviders => ApiKeyScopes.ProvidersRead,
        OrganizationAction.ManageProviders => ApiKeyScopes.ProvidersWrite,
        OrganizationAction.ReadPlans => ApiKeyScopes.PlansRead,
        OrganizationAction.ManagePlans => ApiKeyScopes.PlansWrite,
        OrganizationAction.ReadBudgets => ApiKeyScopes.BudgetsRead,
        OrganizationAction.ManageBudgets => ApiKeyScopes.BudgetsWrite,
        _ => null
    };

    private static bool RoleAllows(OrganizationRole role, OrganizationAction action) => action switch
    {
        OrganizationAction.ReadGitHub => true,
        OrganizationAction.ManageGitHub => role is OrganizationRole.Owner or OrganizationRole.Admin,
        OrganizationAction.ReadProviders or OrganizationAction.ReadPlans => true,
        OrganizationAction.ManageProviders or OrganizationAction.ManagePlans => role is OrganizationRole.Owner or OrganizationRole.Admin,
        OrganizationAction.ReadWorkspaces => true,
        OrganizationAction.ReadBudgets => true,
        OrganizationAction.ManageBudgets => role is OrganizationRole.Owner or OrganizationRole.Admin,
        OrganizationAction.WriteWorkspaces => role is OrganizationRole.Owner or OrganizationRole.Admin or OrganizationRole.Developer,
        OrganizationAction.ReadApiKeys or OrganizationAction.WriteApiKeys or OrganizationAction.ManageMembers => role is OrganizationRole.Owner or OrganizationRole.Admin,
        _ => false
    };
}

public enum OrganizationAction
{
    ReadGitHub,
    ManageGitHub,
    ReadWorkspaces,
    WriteWorkspaces,
    ReadApiKeys,
    WriteApiKeys,
    ManageMembers,
    ReadProviders,
    ManageProviders,
    ReadPlans,
    ManagePlans,
    ReadBudgets,
    ManageBudgets
}
