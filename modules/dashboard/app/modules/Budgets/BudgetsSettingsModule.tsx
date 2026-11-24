import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Disclosure } from "@/core/components/Disclosure";
import { EmptyState } from "@/core/components/EmptyState";
import { PageScaffold } from "@/core/components/PageScaffold";
import { useAuth } from "@/core/auth/AuthProvider";
import { createBudget, deleteBudget, listBudgets, updateBudget, type Budget } from "@/api/budgetsApi";
import { listProviders, type ProviderAccount } from "@/api/providersApi";
import { formatPrice } from "@/modules/Workspaces/WorkspaceCostEstimate";

export function budgetStatus(budget: Budget) {
  const projection = budget.projection;
  if (projection.overBudget) return { label: "Over budget", variant: "destructive" as const };
  if (projection.crossedThreshold !== null) return { label: `Crossed ${projection.crossedThreshold}%`, variant: "secondary" as const };
  return { label: "Within budget", variant: "outline" as const };
}

export function BudgetsSettingsModule() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const canManage = ["owner", "admin"].includes(selectedOrganization?.role?.toLowerCase() ?? "");
  const [budgets, setBudgets] = useState<Budget[] | null>(null);
  const [providers, setProviders] = useState<ProviderAccount[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Budget | "new" | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (!organizationId) return;
    setError(null);
    try {
      const [loaded, accounts] = await Promise.all([
        listBudgets(organizationId, signal),
        listProviders(organizationId, signal).catch(() => [] as ProviderAccount[]),
      ]);
      setBudgets(loaded);
      setProviders(accounts);
    } catch (cause) {
      if (signal?.aborted) return;
      setError(cause instanceof Error ? cause.message : "Budgets could not be loaded.");
    }
  }, [organizationId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "Budgets" }]} title="Budgets"
    actions={canManage && editing === null ? <Button onClick={() => setEditing("new")}>Create budget</Button> : null}>
    <p className="text-sm text-muted-foreground">
      Budgets only inform. Crossing a threshold never blocks creating or starting workspaces and never stops resources.
      All amounts are estimates from recorded rates, not provider invoices.
    </p>
    {!canManage && <p className="text-sm text-muted-foreground">You can view budgets. Owners and admins can change them.</p>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {canManage && editing !== null && organizationId && <BudgetForm organizationId={organizationId}
      budget={editing === "new" ? null : editing} providers={providers}
      onCancel={() => setEditing(null)} onSaved={async () => { setEditing(null); await load(); }} />}
    {budgets === null && !error && <p className="text-sm text-muted-foreground">Loading budgets…</p>}
    {budgets?.length === 0 && <EmptyState title="No budgets yet">
      {canManage ? "Create a monthly budget to compare estimated spend with your thresholds." : "No budgets have been set for this organization."}
    </EmptyState>}
    {budgets?.map(budget => <BudgetCard key={budget.id} budget={budget} canManage={canManage} providers={providers}
      onEdit={() => setEditing(budget)} onDeleted={() => load()} onError={setError} />)}
  </PageScaffold>;
}

function BudgetCard({ budget, canManage, providers, onEdit, onDeleted, onError }: {
  budget: Budget; canManage: boolean; providers: ProviderAccount[];
  onEdit: () => void; onDeleted: () => Promise<void> | void; onError: (message: string) => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const projection = budget.projection;
  const status = budgetStatus(budget);
  const money = (value: number) => formatPrice(value, budget.currency) ?? `${value} ${budget.currency}`;
  const scope = budget.providerAccountIds.length === 0 ? "All provider accounts"
    : budget.providerAccountIds.map(id => providers.find(provider => provider.id === id)?.name ?? id).join(", ");
  async function remove() {
    try { await deleteBudget(budget.id); await onDeleted(); }
    catch (cause) { onError(cause instanceof Error ? cause.message : "Budget could not be deleted."); }
    finally { setConfirming(false); }
  }
  return <Card aria-label={`Budget ${budget.name}`}>
    <CardHeader className="flex-row flex-wrap items-start justify-between gap-2 space-y-0">
      <div className="space-y-1">
        <CardTitle className="flex flex-wrap items-center gap-2">{budget.name}
          <Badge variant={status.variant}>{status.label}</Badge>
          {!projection.complete && <Badge variant="outline">Incomplete estimate</Badge>}
        </CardTitle>
        <CardDescription>{money(budget.monthlyAmount)} per month · {scope} · alerts at {budget.thresholds.map(x => `${x}%`).join(", ")}</CardDescription>
      </div>
      {canManage && <div className="flex gap-2">
        <Button variant="outline" size="sm" onClick={onEdit}>Edit</Button>
        {confirming
          ? <><Button variant="destructive" size="sm" onClick={() => void remove()}>Confirm delete</Button>
            <Button variant="ghost" size="sm" onClick={() => setConfirming(false)}>Cancel</Button></>
          : <Button variant="ghost" size="sm" onClick={() => setConfirming(true)}>Delete</Button>}
      </div>}
    </CardHeader>
    <CardContent className="space-y-3 text-sm">
      <div>
        <p><span className="font-medium">Estimated {money(projection.projectedMonthly)}</span> of {money(budget.monthlyAmount)} for {projection.period} ({projection.percentOfBudget}%)</p>
        <p className="text-muted-foreground">Estimated current run rate {money(projection.projectedHourly)}/hour. Provider-reported charges are not available yet.</p>
        <div className="mt-2 h-2 overflow-hidden rounded bg-muted" role="progressbar" aria-label={`${budget.name} estimated spend`}
          aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.min(100, projection.percentOfBudget)}>
          <div className={`h-full ${projection.overBudget ? "bg-destructive" : "bg-primary"}`} style={{ width: `${Math.min(100, projection.percentOfBudget)}%` }} />
        </div>
      </div>
      {projection.warnings.length > 0 && <ul className="list-disc space-y-1 pl-5 text-amber-700 dark:text-amber-400">
        {projection.warnings.map(warning => <li key={warning}>{warning}</li>)}
      </ul>}
      {projection.providers.length > 0 && <Table aria-label={`${budget.name} estimate by provider`}>
        <TableHeader><TableRow><TableHead>Provider account</TableHead><TableHead>Compute / month</TableHead>
          <TableHead>Storage / month</TableHead><TableHead>Estimated / month</TableHead></TableRow></TableHeader>
        <TableBody>{projection.providers.map(provider => <TableRow key={provider.providerAccountId}>
          <TableCell>{provider.providerName} <span className="text-muted-foreground">({provider.providerType})</span></TableCell>
          <TableCell>{money(provider.computeMonthly)}</TableCell><TableCell>{money(provider.storageMonthly)}</TableCell>
          <TableCell>{money(provider.projectedMonthly)}</TableCell>
        </TableRow>)}</TableBody>
      </Table>}
      {projection.contributors.length > 0 && <Disclosure label={`Contributing workspaces (${projection.contributors.length})`}>
        <Table aria-label={`${budget.name} contributing workspaces`}>
          <TableHeader><TableRow><TableHead>Workspace</TableHead><TableHead>Region / SKU</TableHead>
            <TableHead>Running VMs</TableHead><TableHead>Estimated / month</TableHead><TableHead>Pricing</TableHead></TableRow></TableHeader>
          <TableBody>{projection.contributors.map(item => <TableRow key={item.workspaceId}>
            <TableCell>{item.workspaceName}</TableCell>
            <TableCell>{item.region}{item.skus.length ? ` · ${item.skus.join(", ")}` : ""}</TableCell>
            <TableCell>{item.reservedVmCount}</TableCell>
            <TableCell>{money(item.projectedMonthly)}</TableCell>
            <TableCell>{item.pricingStatus === "current" ? "Current" : item.pricingStatus === "stale" ? "Stale" : "Unknown"}</TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </Disclosure>}
      {projection.excluded.length > 0 && <Disclosure label={`Excluded workspaces (${projection.excluded.length})`}>
        <ul className="list-disc space-y-1 pl-5">{projection.excluded.map(item => <li key={item.workspaceId}>{item.workspaceName}: {item.reason}</li>)}</ul>
      </Disclosure>}
      <Disclosure label="How this estimate is calculated">
        <ul className="list-disc space-y-1 pl-5 text-muted-foreground">{projection.assumptions.map(item => <li key={item}>{item}</li>)}</ul>
      </Disclosure>
    </CardContent>
  </Card>;
}

function BudgetForm({ organizationId, budget, providers, onCancel, onSaved }: {
  organizationId: string; budget: Budget | null; providers: ProviderAccount[];
  onCancel: () => void; onSaved: () => Promise<void> | void;
}) {
  const [name, setName] = useState(budget?.name ?? "");
  const [amount, setAmount] = useState(budget ? String(budget.monthlyAmount) : "");
  const [currency, setCurrency] = useState(budget?.currency ?? "USD");
  const [thresholds, setThresholds] = useState((budget?.thresholds ?? [50, 80, 100]).join(", "));
  const [scope, setScope] = useState<string[]>(budget?.providerAccountIds ?? []);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault();
    const parsed = thresholds.split(",").map(x => x.trim()).filter(Boolean).map(Number);
    if (parsed.some(x => !Number.isInteger(x))) { setError("Thresholds must be whole percentages, for example 50, 80, 100."); return; }
    const input = { name, monthlyAmount: Number(amount), thresholds: parsed, providerAccountIds: scope };
    setSaving(true);
    setError(null);
    try {
      if (budget) await updateBudget(budget.id, { ...input, revision: budget.revision });
      else await createBudget({ ...input, organizationId, currency });
      await onSaved();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Budget could not be saved.");
    } finally { setSaving(false); }
  }
  return <Card>
    <CardHeader><CardTitle>{budget ? `Edit ${budget.name}` : "Create budget"}</CardTitle>
      <CardDescription>Alerts are informational: they are recorded in the audit log and shown here, and never block or stop workspaces.</CardDescription>
    </CardHeader>
    <CardContent>
      <form className="grid gap-4" onSubmit={event => void submit(event)}>
        <div className="grid gap-2"><Label htmlFor="budget-name">Name</Label>
          <Input id="budget-name" value={name} maxLength={128} required onChange={event => setName(event.target.value)} /></div>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="grid gap-2"><Label htmlFor="budget-amount">Monthly amount</Label>
            <Input id="budget-amount" type="number" min="0.01" step="0.01" required value={amount} onChange={event => setAmount(event.target.value)} /></div>
          <div className="grid gap-2"><Label htmlFor="budget-currency">Currency</Label>
            <Input id="budget-currency" value={currency} maxLength={3} required disabled={budget !== null}
              onChange={event => setCurrency(event.target.value.toUpperCase())} /></div>
        </div>
        <div className="grid gap-2"><Label htmlFor="budget-thresholds">Alert thresholds (%)</Label>
          <Input id="budget-thresholds" value={thresholds} onChange={event => setThresholds(event.target.value)} /></div>
        {providers.length > 0 && <fieldset className="grid gap-2"><legend className="text-sm font-medium">Provider accounts</legend>
          <p className="text-xs text-muted-foreground">Leave all unchecked to include every provider account.</p>
          {providers.map(provider => <label key={provider.id} className="flex items-center gap-2 text-sm">
            <Checkbox checked={scope.includes(provider.id)} onCheckedChange={checked =>
              setScope(current => checked ? [...current, provider.id] : current.filter(id => id !== provider.id))} />
            {provider.name} <span className="text-muted-foreground">({provider.providerType})</span>
          </label>)}
        </fieldset>}
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>{budget ? "Save budget" : "Create budget"}</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>Cancel</Button>
        </div>
      </form>
    </CardContent>
  </Card>;
}
