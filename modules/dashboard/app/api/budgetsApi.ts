import { apiRequest } from "./apiClient";

export type BudgetProviderProjection = {
  providerAccountId: string; providerName: string; providerType: string; computeMonthly: number; storageMonthly: number;
  projectedMonthly: number; workspaceCount: number; reservedVmCount: number;
};
export type BudgetContributor = {
  workspaceId: string; workspaceName: string; providerAccountId: string; region: string; skus: string[]; reservedVmCount: number;
  computeHourly: number; computeMonthly: number; storageMonthly: number; projectedMonthly: number;
  pricingStatus: "current" | "stale" | "unknown"; pricingUpdatedAt?: string | null;
};
export type BudgetExcludedWorkspace = { workspaceId: string; workspaceName: string; currency: string; reason: string };
/** Every amount is an estimate from recorded rates; provider-reported charges are not available yet. */
export type BudgetProjection = {
  period: string; estimate: boolean; complete: boolean; projectedHourly: number; projectedMonthly: number; percentOfBudget: number;
  crossedThreshold: number | null; overBudget: boolean; providerChargesAvailable: boolean;
  providers: BudgetProviderProjection[]; contributors: BudgetContributor[]; excluded: BudgetExcludedWorkspace[];
  warnings: string[]; assumptions: string[];
};
export type Budget = {
  id: string; organizationId: string; name: string; currency: string; monthlyAmount: number; thresholds: number[];
  providerAccountIds: string[]; revision: number; createdAt: string; updatedAt: string; projection: BudgetProjection;
};
export type BudgetInput = { name: string; monthlyAmount: number; thresholds: number[]; providerAccountIds: string[] };

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
