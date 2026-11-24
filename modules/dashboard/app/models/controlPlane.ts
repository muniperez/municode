import type { DevelopmentWorkspaceVmResources } from "@/api/workspaceFilesystemApi";

export type OrganizationSummary = {
  id: string;
  name: string;
  slug: string;
  role: string;
};

export type MeResponse = {
  id: string;
  email: string;
  displayName?: string | null;
  organizations: OrganizationSummary[];
};

export type OrganizationResponse = {
  id: string;
  name: string;
  slug: string;
  createdAt: string;
};

export type ApiKeyScope =
  | "github:read"
  | "github:write"
  | "workspaces:read"
  | "workspaces:write"
  | "api_keys:read"
  | "api_keys:write"
  | "providers:read"
  | "providers:write"
  | "plans:read"
  | "plans:write";

export type ApiKeyResponse = {
  id: string;
  name: string;
  prefix: string;
  scopes: ApiKeyScope[];
  lastUsedAt?: string | null;
  expiresAt?: string | null;
  revokedAt?: string | null;
  createdAt: string;
};

export type ApiKeyCreateResponse = ApiKeyResponse & {
  token: string;
};

export type CliLoginSessionResponse = {
  sessionId: string;
  status: "pending" | "approved" | "exchanged" | "expired";
  scopes: ApiKeyScope[];
  expiresAt: string;
  approvedAt?: string | null;
  exchangedAt?: string | null;
};

export type ApiErrorDetail = {
  field: string;
  message: string;
};

export type ApiErrorBody = {
  code: string;
  message: string;
  details: ApiErrorDetail[];
};

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly details: ApiErrorDetail[];

  constructor(status: number, error: ApiErrorBody) {
    super(error.message);
    this.name = "ApiError";
    this.status = status;
    this.code = error.code;
    this.details = error.details;
  }

  fieldError(field: string) {
    return this.details.find((detail) => detail.field === field)?.message;
  }
}

export type DevelopmentWorkspace = {
  id: string;
  organizationId: string;
  createdBy?: string | null;
  idleTimeoutMinutes?: number | null;
  effectiveIdleTimeoutMinutes?: number | null;
  keepRunning?: boolean;
  name: string;
  imageId: string;
  profile: string;
  planId: string;
  providerAccountId: string;
  region: string;
  sku: string;
  volumeSizeGb: number;
  desiredState: "running" | "stopped";
  phase: string;
  generation: number;
  providerServerId?: string | null;
  providerVolumeId?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  lastActivityAt?: string | null;
  shutdownDeadline?: string | null;
  lastReadyAt?: string | null;
  estimatedMonthlyCompute: number;
  estimatedMonthlyStorage: number;
  currency: string;
  pricingUpdatedAt?: string | null;
  revision: number;
  createdAt: string;
  updatedAt: string;
  primaryVmId?: string | null;
  volumeStatus?: string | null;
  agents?: { id: string; name: string; version: string }[] | null;
  vms: DevelopmentWorkspaceVm[];
};

export type DevelopmentWorkspaceTaskLease = {
  vmId: string;
  taskId: string;
  generation: number;
  status: string;
  expiresAt: string;
};

export type DevelopmentWorkspaceVm = {
  id: string;
  kind: "primary" | "ephemeral";
  generation: number;
  desiredState: string;
  phase: string;
  imageId: string;
  profile: string;
  planId: string;
  providerAccountId: string;
  region: string;
  sku: string;
  providerServerId?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  lastActivityAt?: string | null;
  shutdownDeadline?: string | null;
  lastReadyAt?: string | null;
  repository?: string | null;
  baseRef?: string | null;
  baseCommit?: string | null;
  workBranch?: string | null;
  pushAuthorized: boolean;
  ignoredDataAcknowledged: boolean;
  gitStatus: string;
  finalHead?: string | null;
  verifiedRemoteRef?: string | null;
  verifiedRemoteCommit?: string | null;
  nextImageId?: string | null;
  nextProfile?: string | null;
  nextPlanId?: string | null;
  computeHourlyRate: number;
  estimatedMonthlyCompute: number;
  computeReserved: boolean;
  revision: number;
  taskLeases?: DevelopmentWorkspaceTaskLease[];
  startAfterStop?: boolean;
  startupStage?: string | null;
  resources?: DevelopmentWorkspaceVmResources | null;
  /** Status of the VM's current start operation; "pending"/"running" means it is still active or retrying. */
  startupOperationStatus?: "pending" | "running" | "failed" | null;
};
