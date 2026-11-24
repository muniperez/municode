// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Municode.Cli.Contracts;

public sealed record OrganizationSummary(string Id, string Name, string Slug, string Role);
public sealed record MeResponse(string Id, string Email, string? DisplayName, IReadOnlyList<OrganizationSummary> Organizations);
public sealed record ErrorResponse(ErrorBody Error);
public sealed record ErrorBody(string Code, string Message, IReadOnlyList<ErrorDetail> Details);
public sealed record ErrorDetail(string Field, string Message);

public sealed record CliLoginSessionCreateResponse(
    string SessionId, string LoginUrl, string Status, DateTimeOffset ExpiresAt);
public sealed record CliLoginSessionExchangeResponse(
    string Token, string Email, OrganizationSummary Organization, IReadOnlyList<string> Scopes);

public sealed record WorkspaceSelectionOption(
    string ImageId, string ImageName, string Profile, string PlanId, string PlanName,
    string ProviderAccountName, string Region, string Sku, int CpuCount, int MemoryGiB,
    decimal ComputeHourlyRate, bool IsDefault,
    IReadOnlyList<DevelopmentWorkspaceAgentResponse>? Agents = null);
public sealed record DevelopmentWorkspaceAgentResponse(string Id, string Name, string Version);
public sealed record WorkspaceSelections(
    bool Available, string? UnavailableReason, IReadOnlyList<WorkspaceSelectionOption> Options,
    string? Currency = null, decimal? VolumeMonthlyRatePerGb = null);

public sealed record CreateDevelopmentWorkspaceRequest(
    string OrganizationId, string Name, string ImageId, string Profile, string PlanId,
    int VolumeSizeGb, string IdempotencyKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IdleTimeoutMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool KeepRunning = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool StartPrimaryVm = false);
public sealed record UpdateDevelopmentWorkspaceIdleTimeoutRequest(int? IdleTimeoutMinutes, bool KeepRunning = false);
public sealed record DevelopmentWorkspaceGenerationRequest(long Generation);
public sealed record DevelopmentWorkspaceResizeRequest(long Generation, string ImageId, string Profile, string PlanId);
public sealed record DevelopmentWorkspaceDiscardRequest(
    long Generation, bool DiscardLocalData, bool AcknowledgeIgnoredDataLoss);
public sealed record CompleteDevelopmentWorkspaceTaskRequest(long Generation, bool Cancelled = false);
public sealed record LaunchEphemeralDevelopmentWorkspaceVmRequest(
    string ImageId, string Profile, string PlanId, string Repository, string BaseRef, string BaseCommit,
    bool PushAuthorized, bool AcknowledgeIgnoredDataLoss, string IdempotencyKey,
    string? RepositoryCredentialRef = null);
public sealed record DevelopmentWorkspaceGatewayLaunchRequest(long Generation, string Service = "t3");
public sealed record DevelopmentWorkspaceGatewayLaunchResponse(string Url, DateTimeOffset ExpiresAt);
public sealed record DevelopmentWorkspaceTaskLeaseResponse(
    string VmId, string TaskId, long Generation, string Status, DateTimeOffset ExpiresAt);

public sealed record DevelopmentWorkspaceResponse(
    string Id, string OrganizationId, string Name, string ImageId, string Profile, string PlanId,
    string ProviderAccountId, string Region, string Sku, int VolumeSizeGb, string DesiredState, string Phase,
    long Generation, string? ErrorCode, string? ErrorMessage, decimal EstimatedMonthlyCompute,
    decimal EstimatedMonthlyStorage, string Currency, DateTimeOffset? PricingUpdatedAt,
    IReadOnlyList<DevelopmentWorkspaceVmResponse>? Vms = null, string? VolumeStatus = null,
    int? IdleTimeoutMinutes = null, int? EffectiveIdleTimeoutMinutes = null, bool KeepRunning = false,
    IReadOnlyList<DevelopmentWorkspaceAgentResponse>? Agents = null);

public sealed record DevelopmentWorkspaceVmResponse(
    string Id, string Kind, long Generation, string DesiredState, string Phase,
    string ImageId, string Profile, string PlanId, string ProviderAccountId, string Region, string Sku,
    string? ErrorCode, string? ErrorMessage, DateTimeOffset? LastActivityAt, DateTimeOffset? ShutdownDeadline,
    string? Repository, string? BaseRef, string? BaseCommit, string? WorkBranch,
    bool PushAuthorized, bool IgnoredDataAcknowledged, string GitStatus,
    string? VerifiedRemoteRef, string? VerifiedRemoteCommit,
    string? NextImageId, string? NextProfile, string? NextPlanId,
    decimal ComputeHourlyRate, decimal EstimatedMonthlyCompute, bool ComputeReserved,
    IReadOnlyList<DevelopmentWorkspaceTaskLeaseResponse>? TaskLeases = null,
    bool StartAfterStop = false, string? StartupStage = null, string? StartupOperationStatus = null);
