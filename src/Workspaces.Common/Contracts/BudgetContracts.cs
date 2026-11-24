namespace Workspaces.Common.Contracts;

public sealed record CreateBudgetRequest(string OrganizationId, string Name, decimal MonthlyAmount, string? Currency = null,
    IReadOnlyList<int>? Thresholds = null, IReadOnlyList<string>? ProviderAccountIds = null);

public sealed record UpdateBudgetRequest(long Revision, string Name, decimal MonthlyAmount,
    IReadOnlyList<int>? Thresholds = null, IReadOnlyList<string>? ProviderAccountIds = null);

public sealed record BudgetResponse(string Id, string OrganizationId, string Name, string Currency, decimal MonthlyAmount,
    IReadOnlyList<int> Thresholds, IReadOnlyList<string> ProviderAccountIds, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, BudgetProjectionResponse Projection);

// Every amount is an estimate derived from recorded rates; provider-reported charges are not available yet.
public sealed record BudgetProjectionResponse(string Period, bool Estimate, bool Complete, decimal ProjectedHourly,
    decimal ProjectedMonthly, decimal PercentOfBudget, int? CrossedThreshold, bool OverBudget, bool ProviderChargesAvailable,
    IReadOnlyList<BudgetProviderProjectionResponse> Providers, IReadOnlyList<BudgetContributorResponse> Contributors,
    IReadOnlyList<BudgetExcludedWorkspaceResponse> Excluded, IReadOnlyList<string> Warnings, IReadOnlyList<string> Assumptions);

public sealed record BudgetProviderProjectionResponse(string ProviderAccountId, string ProviderName, string ProviderType,
    decimal ComputeMonthly, decimal StorageMonthly, decimal ProjectedMonthly, int WorkspaceCount, int ReservedVmCount);

public sealed record BudgetContributorResponse(string WorkspaceId, string WorkspaceName, string ProviderAccountId, string Region,
    IReadOnlyList<string> Skus, int ReservedVmCount, decimal ComputeHourly, decimal ComputeMonthly, decimal StorageMonthly,
    decimal ProjectedMonthly, string PricingStatus, DateTimeOffset? PricingUpdatedAt);

public sealed record BudgetExcludedWorkspaceResponse(string WorkspaceId, string WorkspaceName, string Currency, string Reason);
