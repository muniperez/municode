import { apiRequest } from "./apiClient";

/** Calendar month, ISO week (Monday to Sunday) or calendar quarter, all in UTC. */
export type BudgetPeriod = "month" | "week" | "quarter";

export type BudgetProviderProjection = {
  providerAccountId: string; providerName: string; providerType: string; computeMonthly: number; storageMonthly: number;
  projectedMonthly: number; workspaceCount: number; reservedVmCount: number;
  /** False when a resource has a missing price; its cost is left out, never counted as zero. */
  complete: boolean; assumptions: string[];
  /** Estimate for the whole budget period; the *Monthly fields are the monthly run rate. */
  projectedAmount: number;
};
/** One priced resource: from the operator's price catalog or the rate recorded when it was selected (fallback). */
export type BudgetPriceLine = {
  resource: "compute" | "storage"; resourceId: string; sku?: string | null; quantity: number; source: "catalog" | "fallback";
  catalogEntryId?: string | null; billingModel: string; currency: string; amount: number | null;
  status: "current" | "stale" | "missing"; pricedAt?: string | null;
  /** Estimate for the budget period; amount is the monthly run rate. */
  periodAmount: number | null;
};
export type BudgetContributor = {
  workspaceId: string; workspaceName: string; providerAccountId: string; region: string; skus: string[]; reservedVmCount: number;
  computeHourly: number; computeMonthly: number; storageMonthly: number; projectedMonthly: number;
  pricingStatus: "current" | "stale" | "unknown"; pricingUpdatedAt?: string | null; lines: BudgetPriceLine[];
  projectedAmount: number;
};
export type BudgetExcludedWorkspace = { workspaceId: string; workspaceName: string; currency: string; reason: string };
/**
 * Every amount is an estimate from recorded rates; provider-reported charges are never mixed in (see Budget.providerCharges).
 * `period` is the key of the current period ("2026-11", "2026-W48" or "2026-Q4"); `projectedAmount` is the estimate for that
 * whole period and is what the percentage and thresholds compare with the budget amount.
 */
export type BudgetProjection = {
  period: string; estimate: boolean; complete: boolean; projectedHourly: number; projectedMonthly: number; percentOfBudget: number;
  crossedThreshold: number | null; overBudget: boolean; providerChargesAvailable: boolean;
  providers: BudgetProviderProjection[]; contributors: BudgetContributor[]; excluded: BudgetExcludedWorkspace[];
  warnings: string[]; assumptions: string[];
  periodType: BudgetPeriod; periodStart: string; periodEnd: string; periodHours: number; projectedAmount: number;
};
export type BudgetEmailAudience = "admins" | "members";
/**
 * The full webhook URL is write-only; only its scheme, host and port are returned.
 * emailAvailable is false until the operator configures outbound email; until then no email is sent.
 */
export type BudgetNotificationSettings = {
  inApp: boolean; webhook: boolean; webhookTarget: string | null; webhookSecretConfigured: boolean;
  email: boolean; emailAudience: BudgetEmailAudience; emailAvailable: boolean;
};
export type BudgetNotificationDelivery = {
  id: string; channel: "in_app" | "webhook" | "email"; period: string; threshold: number;
  status: "pending" | "delivered" | "failed" | "cancelled"; attempts: number; lastError: string | null;
  createdAt: string; deliveredAt: string | null; nextAttemptAt: string | null;
};
export type BudgetProviderChargeStatus = "available" | "stale" | "failed" | "unsupported" | "not_retrieved";
/** Month-to-date charges a provider reported for a whole provider account. amount is null, never zero, when nothing was reported. */
export type BudgetProviderCharge = {
  providerAccountId: string; providerName: string; providerType: string; status: BudgetProviderChargeStatus;
  amount: number | null; currency: string | null; asOf: string | null; attemptedAt: string | null; source: string | null; message: string;
};
/** Totals are per currency; different currencies are never summed or converted. */
export type BudgetProviderCharges = {
  period: string; complete: boolean; accounts: BudgetProviderCharge[];
  totals: { currency: string; amount: number; accountCount: number }[]; notes: string[];
};
export type Budget = {
  id: string; organizationId: string; name: string; currency: string; amount: number; period: BudgetPeriod; thresholds: number[];
  /** Scope: provider accounts, then whole workspaces or individual VMs (compute only); empty lists include everything. */
  providerAccountIds: string[]; workspaceIds: string[]; vmIds: string[]; revision: number; createdAt: string; updatedAt: string; projection: BudgetProjection;
  notifications: BudgetNotificationSettings; lastAlert: { period: string; threshold: number; alertedAt: string } | null;
  deliveries: BudgetNotificationDelivery[];
  providerCharges?: BudgetProviderCharges | null;
  /** Only present on the create/update response that generated or rotated the signing secret. */
  webhookSigningSecret?: string | null;
};
/** Omitted fields keep their current value; webhookUrl "" removes the webhook. */
export type BudgetNotificationInput = {
  inApp?: boolean; webhook?: boolean; webhookUrl?: string; rotateWebhookSecret?: boolean;
  email?: boolean; emailAudience?: BudgetEmailAudience;
};
export type BudgetInput = {
  name: string; amount: number; period: BudgetPeriod; thresholds: number[]; providerAccountIds: string[]; workspaceIds: string[];
  vmIds: string[]; notifications?: BudgetNotificationInput;
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
