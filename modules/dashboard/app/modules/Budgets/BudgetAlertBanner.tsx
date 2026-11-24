import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/core/auth/AuthProvider";
import { useSettings } from "@/modules/Settings/SettingsProvider";
import { listBudgets, type Budget } from "@/api/budgetsApi";
import { formatPrice } from "@/modules/Workspaces/WorkspaceCostEstimate";

const periodPhrase = { month: "this month", week: "this week", quarter: "this quarter" } as const;
const alertKey = (budget: Budget) => `${budget.id}:${budget.projection.period}:${budget.projection.crossedThreshold}`;
const storageKey = (organizationId: string) => `municode:budget-alerts-dismissed:${organizationId}`;

function dismissedAlerts(organizationId: string): string[] {
  try { return JSON.parse(sessionStorage.getItem(storageKey(organizationId)) ?? "[]") as string[]; }
  catch { return []; }
}

/** Non-blocking notice for crossed budget thresholds; budgets never block or stop workspaces. */
export function BudgetAlertBanner() {
  const { selectedOrganization } = useAuth();
  const { openSettings } = useSettings();
  const organizationId = selectedOrganization?.id;
  const [alerts, setAlerts] = useState<Budget[]>([]);
  useEffect(() => {
    setAlerts([]);
    if (!organizationId) return;
    const controller = new AbortController();
    listBudgets(organizationId, controller.signal).then(budgets => {
      const dismissed = dismissedAlerts(organizationId);
      setAlerts(budgets.filter(budget => budget.notifications.inApp && budget.projection.crossedThreshold !== null
        && !dismissed.includes(alertKey(budget))));
    }).catch(() => {});
    return () => controller.abort();
  }, [organizationId]);
  if (!organizationId || alerts.length === 0) return null;
  const [first] = alerts;
  const amount = formatPrice(first.projection.projectedAmount, first.currency);
  const limit = formatPrice(first.amount, first.currency);
  function dismiss() {
    sessionStorage.setItem(storageKey(organizationId!), JSON.stringify([...dismissedAlerts(organizationId!), ...alerts.map(alertKey)]));
    setAlerts([]);
  }
  return <div role="status" className="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-100">
    <p>
      Budget “{first.name}” is estimated at {amount} of {limit} {periodPhrase[first.period]} ({first.projection.percentOfBudget}%, crossed {first.projection.crossedThreshold}%)
      {alerts.length > 1 ? ` and ${alerts.length - 1} more budget${alerts.length > 2 ? "s" : ""} crossed a threshold` : ""}.
      {" "}Budgets only inform; nothing was blocked or stopped.
    </p>
    <div className="flex gap-2">
      <Button size="sm" variant="outline" onClick={event => openSettings("budgets", event.currentTarget)}>Review budgets</Button>
      <Button size="sm" variant="ghost" onClick={dismiss}>Dismiss</Button>
    </div>
  </div>;
}
