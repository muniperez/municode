namespace Workspaces.Common.Services;

public static class ApiKeyScopes
{
    public const string GitHubRead = "github:read";
    public const string GitHubWrite = "github:write";
    public const string WorkspacesRead = "workspaces:read";
    public const string WorkspacesWrite = "workspaces:write";
    public const string ApiKeysRead = "api_keys:read";
    public const string ApiKeysWrite = "api_keys:write";
    public const string ProvidersRead = "providers:read";
    public const string ProvidersWrite = "providers:write";
    public const string PlansRead = "plans:read";
    public const string PlansWrite = "plans:write";
    public const string BudgetsRead = "budgets:read";
    public const string BudgetsWrite = "budgets:write";

    public static readonly IReadOnlyList<string> CliLoginDefault =
        [GitHubRead, WorkspacesRead, WorkspacesWrite, PlansRead];

    public static readonly ISet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        GitHubRead,
        GitHubWrite,
        WorkspacesRead,
        WorkspacesWrite,
        ApiKeysRead,
        ApiKeysWrite,
        ProvidersRead,
        ProvidersWrite,
        PlansRead,
        PlansWrite,
        BudgetsRead,
        BudgetsWrite
    };
}
