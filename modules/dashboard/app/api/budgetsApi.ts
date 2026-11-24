import { apiRequest } from "./apiClient";

export type BudgetProviderProjection = {
  providerAccountId: string; providerName: string; providerType: string; computeMonthly: number; storageMonthly: number;
  projectedMonthly: number; workspaceCount: number; reservedVmCount: number;
  /** False when a resource has a missing price; its cost is left out, never counted as zero. */
  complete: boolean; assumptions: string[];
};
/** One priced resource: from the operator's price catalog or the rate recorded when it was selected (fallback). */
export type BudgetPriceLine = {
  resource: "compute" | "storage"; resourceId: string; sku?: string | null; quantity: number; source: "catalog" | "fallback";
  catalogEntryId?: string | null; billingModel: string; currency: string; amount: number | null;
  status: "current" | "stale" | "missing"; pricedAt?: string | null;
};
export type BudgetContributor = {
  workspaceId: string; workspaceName: string; providerAccountId: string; region: string; skus: string[]; reservedVmCount: number;
  computeHourly: number; computeMonthly: number; storageMonthly: number; projectedMonthly: number;
  pricingStatus: "current" | "stale" | "unknown"; pricingUpdatedAt?: string | null; lines: BudgetPriceLine[];
};
export type BudgetExcludedWorkspace = { workspaceId: string; workspaceName: string; currency: string; reason: string };
/** Every amount is an estimate from recorded rates; provider-reported charges are not available yet. */
export type BudgetProjection = {
  period: string; estimate: boolean; complete: boolean; projectedHourly: number; projectedMonthly: number; percentOfBudget: number;
  crossedThreshold: number | null; overBudget: boolean; providerChargesAvailable: boolean;
  providers: BudgetProviderProjection[]; contributors: BudgetContributor[]; excluded: BudgetExcludedWorkspace[];
  warnings: string[]; assumptions: string[];
};
/** The full webhook URL is write-only; only its scheme, host and port are returned. */
export type BudgetNotificationSettings = { inApp: boolean; webhook: boolean; webhookTarget: string | null; webhookSecretConfigured: boolean };
export type BudgetNotificationDelivery = {
  id: string; channel: "in_app" | "webhook"; period: string; threshold: number;
  status: "pending" | "delivered" | "failed" | "cancelled"; attempts: number; lastError: string | null;
  createdAt: string; deliveredAt: string | null; nextAttemptAt: string | null;
};
export type Budget = {
  id: string; organizationId: string; name: string; currency: string; monthlyAmount: number; thresholds: number[];
  providerAccountIds: string[]; revision: number; createdAt: string; updatedAt: string; projection: BudgetProjection;
  notifications: BudgetNotificationSettings; lastAlert: { period: string; threshold: number; alertedAt: string } | null;
  deliveries: BudgetNotificationDelivery[];
  /** Only present on the create/update response that generated or rotated the signing secret. */
  webhookSigningSecret?: string | null;
};
/** Omitted fields keep their current value; webhookUrl "" removes the webhook. */
export type BudgetNotificationInput = { inApp?: boolean; webhook?: boolean; webhookUrl?: string; rotateWebhookSecret?: boolean };
export type BudgetInput = {
  name: string; monthlyAmount: number; thresholds: number[]; providerAccountIds: string[]; notifications?: BudgetNotificationInput;
};

export function listBudgets(organizationId: string, signal?: AbortSignal) {
  return apiRequest<Budget[]>(`/api/budgets?${new URLSearchParams({ organizationId })}`, { signal });
}
export function createBudget(body: BudgetInput & { organizationId: string; currency?: string }) {
  return apiRequest<Budget>("/api/budgets", { method: "POST", body });
}
export function updateBudget(id: string, body: BudgetInput & { revision: number }) {
  return apiRequest<Budget>(`/api/budgets/${encodeURIComponent(id)}`, { method: "PUT", body });
}
export function deleteBudget(id: string) { return apiRequest<void>(`/api/budgets/${encodeURIComponent(id)}`, { method: "DELETE" }); }
