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
import {
  createBudget, deleteBudget, listBudgets, updateBudget, type Budget, type BudgetContributor, type BudgetEmailAudience,
  type BudgetNotificationInput, type BudgetPeriod, type BudgetProjection,
} from "@/api/budgetsApi";
import { listProviders, type ProviderAccount } from "@/api/providersApi";
import { listDevelopmentWorkspaces } from "@/api/workspacesApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { formatPrice } from "@/modules/Workspaces/WorkspaceCostEstimate";
import { ProviderChargesSection } from "./ProviderChargesSection";

export function budgetStatus(budget: Budget) {
  const projection = budget.projection;
  if (projection.overBudget) return { label: "Over budget", variant: "destructive" as const };
  if (projection.crossedThreshold !== null) return { label: `Crossed ${projection.crossedThreshold}%`, variant: "secondary" as const };
  return { label: "Within budget", variant: "outline" as const };
}

const periodLabels = { month: "Monthly", week: "Weekly", quarter: "Quarterly" } as const;
const periodNouns = { month: "month", week: "week", quarter: "quarter" } as const;
const selectClass = "h-10 w-full rounded-md border border-input bg-background px-3 text-sm";

/** For example "2026-W49 (Nov 30 – Dec 6, UTC)"; the end is exclusive. */
export function periodLabel(projection: BudgetProjection) {
  const day = (value: Date) => value.toLocaleDateString("en-US", { month: "short", day: "numeric", timeZone: "UTC" });
  const start = new Date(projection.periodStart);
  const last = new Date(new Date(projection.periodEnd).getTime() - 1);
  return `${projection.period} (${day(start)} – ${day(last)}, UTC)`;
}

export function scopeLabel(budget: Budget, providers: ProviderAccount[], workspaces: DevelopmentWorkspace[]) {
  const parts = [budget.providerAccountIds.length === 0 ? "All provider accounts"
    : budget.providerAccountIds.map(id => providers.find(provider => provider.id === id)?.name ?? id).join(", ")];
  if (budget.workspaceIds.length > 0)
    parts.push(`workspaces ${budget.workspaceIds.map(id => workspaces.find(item => item.id === id)?.name ?? id).join(", ")}`);
  if (budget.vmIds.length > 0) {
    const names = budget.vmIds.map(id => {
      const workspace = workspaces.find(item => item.vms.some(vm => vm.id === id));
      const vm = workspace?.vms.find(item => item.id === id);
      return workspace && vm ? `${workspace.name} ${vm.kind} VM` : id;
    });
    parts.push(`VMs ${names.join(", ")}`);
  }
  return parts.join(" · ");
}

export function pricingLabel(item: BudgetContributor) {
  const status = item.pricingStatus === "current" ? "Current" : item.pricingStatus === "stale" ? "Stale" : "Missing price";
  return item.lines.some(line => line.source === "fallback" && line.status !== "missing") ? `${status} · fallback rate` : status;
}

export function BudgetsSettingsModule() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const canManage = ["owner", "admin"].includes(selectedOrganization?.role?.toLowerCase() ?? "");
  const [budgets, setBudgets] = useState<Budget[] | null>(null);
  const [providers, setProviders] = useState<ProviderAccount[]>([]);
  const [workspaces, setWorkspaces] = useState<DevelopmentWorkspace[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Budget | "new" | null>(null);
  const [revealed, setRevealed] = useState<{ name: string; secret: string } | null>(null);

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

  // Workspaces are only needed to pick a scope or to name scoped workspaces and VMs.
  const needsWorkspaces = editing !== null || (budgets?.some(item => item.workspaceIds.length > 0 || item.vmIds.length > 0) ?? false);
  useEffect(() => {
    if (!organizationId || !needsWorkspaces) return;
    const controller = new AbortController();
    listDevelopmentWorkspaces(organizationId, controller.signal).then(setWorkspaces).catch(() => {});
    return () => controller.abort();
  }, [organizationId, needsWorkspaces]);

  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "Budgets" }]} title="Budgets"
    actions={canManage && editing === null ? <Button onClick={() => setEditing("new")}>Create budget</Button> : null}>
    <p className="text-sm text-muted-foreground">
      Budgets only inform. Crossing a threshold never blocks creating or starting workspaces and never stops resources.
      All amounts are estimates from the operator's price catalog or recorded rates, not provider invoices.
    </p>
    {!canManage && <p className="text-sm text-muted-foreground">You can view budgets. Owners and admins can change them.</p>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {canManage && editing !== null && organizationId && <BudgetForm organizationId={organizationId}
      budget={editing === "new" ? null : editing} providers={providers} workspaces={workspaces}
      onCancel={() => setEditing(null)} onSaved={async saved => {
        setEditing(null);
        if (saved.webhookSigningSecret) setRevealed({ name: saved.name, secret: saved.webhookSigningSecret });
        await load();
      }} />}
    {revealed && <Card role="region" aria-label="Webhook signing secret">
      <CardHeader><CardTitle>Webhook signing secret for {revealed.name}</CardTitle>
        <CardDescription>Copy it now; it is not shown again. Verify each delivery's X-Municode-Signature header with it.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-wrap items-center gap-2">
        <code className="break-all rounded bg-muted px-2 py-1 text-xs">{revealed.secret}</code>
        <Button size="sm" variant="outline" onClick={() => setRevealed(null)}>Done</Button>
      </CardContent>
    </Card>}
    {budgets === null && !error && <p className="text-sm text-muted-foreground">Loading budgets…</p>}
    {budgets?.length === 0 && <EmptyState title="No budgets yet">
      {canManage ? "Create a monthly, weekly or quarterly budget to compare estimated spend with your thresholds." : "No budgets have been set for this organization."}
    </EmptyState>}
    {budgets?.map(budget => <BudgetCard key={budget.id} budget={budget} canManage={canManage} providers={providers} workspaces={workspaces}
      onEdit={() => setEditing(budget)} onDeleted={() => load()} onError={setError} />)}
  </PageScaffold>;
}

function BudgetCard({ budget, canManage, providers, workspaces, onEdit, onDeleted, onError }: {
  budget: Budget; canManage: boolean; providers: ProviderAccount[]; workspaces: DevelopmentWorkspace[];
  onEdit: () => void; onDeleted: () => Promise<void> | void; onError: (message: string) => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const projection = budget.projection;
  const status = budgetStatus(budget);
  const money = (value: number) => formatPrice(value, budget.currency) ?? `${value} ${budget.currency}`;
  const scope = scopeLabel(budget, providers, workspaces);
  const monthly = budget.period === "month";
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
        <CardDescription>{money(budget.amount)} per {periodNouns[budget.period]} · {scope} · alerts at {budget.thresholds.map(x => `${x}%`).join(", ")}</CardDescription>
        <CardDescription>Notifications: {channels(budget)}
          {budget.lastAlert ? ` · last alert ${budget.lastAlert.threshold}% for ${budget.lastAlert.period}` : ""}</CardDescription>
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
        <p><span className="font-medium">Estimated {money(projection.projectedAmount)}</span> of {money(budget.amount)} for {periodLabel(projection)} ({projection.percentOfBudget}%)</p>
        <p className="text-muted-foreground">Estimated current run rate {money(projection.projectedHourly)}/hour
          {monthly ? "" : ` (${money(projection.projectedMonthly)}/month)`}. The estimate does not include provider-reported charges.</p>
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
          <TableHead>Storage / month</TableHead><TableHead>Estimated / month</TableHead>
          {!monthly && <TableHead>Estimated / {periodNouns[budget.period]}</TableHead>}</TableRow></TableHeader>
        <TableBody>{projection.providers.map(provider => <TableRow key={provider.providerAccountId}>
          <TableCell>{provider.providerName} <span className="text-muted-foreground">({provider.providerType})</span>
            {!provider.complete && <Badge variant="outline" className="ml-2">Missing prices</Badge>}</TableCell>
          <TableCell>{money(provider.computeMonthly)}</TableCell><TableCell>{money(provider.storageMonthly)}</TableCell>
          <TableCell>{money(provider.projectedMonthly)}</TableCell>
          {!monthly && <TableCell>{money(provider.projectedAmount)}</TableCell>}
        </TableRow>)}</TableBody>
      </Table>}
      {projection.providers.some(provider => provider.assumptions.length > 0) && <Disclosure label="Pricing assumptions by provider">
        <div className="space-y-2">{projection.providers.filter(provider => provider.assumptions.length > 0).map(provider =>
          <section key={provider.providerAccountId} aria-label={`${provider.providerName} pricing assumptions`}>
            <p className="font-medium">{provider.providerName} <span className="text-muted-foreground">({provider.providerType})</span></p>
            <ul className="list-disc space-y-1 pl-5 text-muted-foreground">{provider.assumptions.map(item => <li key={item}>{item}</li>)}</ul>
          </section>)}</div>
      </Disclosure>}
      {projection.contributors.length > 0 && <Disclosure label={`Contributing workspaces (${projection.contributors.length})`}>
        <Table aria-label={`${budget.name} contributing workspaces`}>
          <TableHeader><TableRow><TableHead>Workspace</TableHead><TableHead>Region / SKU</TableHead>
            <TableHead>Running VMs</TableHead><TableHead>Estimated / month</TableHead>
            {!monthly && <TableHead>Estimated / {periodNouns[budget.period]}</TableHead>}<TableHead>Pricing</TableHead></TableRow></TableHeader>
          <TableBody>{projection.contributors.map(item => <TableRow key={item.workspaceId}>
            <TableCell>{item.workspaceName}</TableCell>
            <TableCell>{item.region}{item.skus.length ? ` · ${item.skus.join(", ")}` : ""}</TableCell>
            <TableCell>{item.reservedVmCount}</TableCell>
            <TableCell>{money(item.projectedMonthly)}</TableCell>
            {!monthly && <TableCell>{money(item.projectedAmount)}</TableCell>}
            <TableCell>{pricingLabel(item)}</TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </Disclosure>}
      {projection.excluded.length > 0 && <Disclosure label={`Excluded workspaces (${projection.excluded.length})`}>
        <ul className="list-disc space-y-1 pl-5">{projection.excluded.map(item => <li key={item.workspaceId}>{item.workspaceName}: {item.reason}</li>)}</ul>
      </Disclosure>}
      <ProviderChargesSection budget={budget} />
      {budget.deliveries.length > 0 && <Disclosure label={`Recent notifications (${budget.deliveries.length})`}>
        <Table aria-label={`${budget.name} recent notifications`}>
          <TableHeader><TableRow><TableHead>Channel</TableHead><TableHead>Threshold</TableHead>
            <TableHead>Status</TableHead><TableHead>Attempts</TableHead><TableHead>Details</TableHead></TableRow></TableHeader>
          <TableBody>{budget.deliveries.map(item => <TableRow key={item.id}>
            <TableCell>{channelLabel[item.channel]}</TableCell>
            <TableCell>{item.threshold}% · {item.period}</TableCell>
            <TableCell>{deliveryStatus[item.status]}</TableCell>
            <TableCell>{item.attempts}</TableCell>
            <TableCell>{item.lastError ?? ""}{item.nextAttemptAt ? `${item.lastError ? " · " : ""}next try ${new Date(item.nextAttemptAt).toLocaleString()}` : ""}</TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </Disclosure>}
      <Disclosure label="How this estimate is calculated">
        <ul className="list-disc space-y-1 pl-5 text-muted-foreground">{projection.assumptions.map(item => <li key={item}>{item}</li>)}</ul>
      </Disclosure>
    </CardContent>
  </Card>;
}

const deliveryStatus = { pending: "Retrying", delivered: "Delivered", failed: "Failed", cancelled: "Cancelled" } as const;

const channelLabel = { in_app: "In-app", webhook: "Webhook", email: "Email" } as const;
const audienceLabel = { admins: "owners and admins", members: "all members" } as const;

function channels(budget: Budget) {
  const { notifications } = budget;
  const enabled = [notifications.inApp ? "in-app" : null,
    notifications.webhook ? `webhook to ${notifications.webhookTarget}` : null,
    notifications.email ? `email to ${audienceLabel[notifications.emailAudience]}${notifications.emailAvailable ? "" : " (not configured)"}` : null,
  ].filter(Boolean);
  return enabled.length ? enabled.join(", ") : "off";
}

function BudgetForm({ organizationId, budget, providers, workspaces, onCancel, onSaved }: {
  organizationId: string; budget: Budget | null; providers: ProviderAccount[]; workspaces: DevelopmentWorkspace[];
  onCancel: () => void; onSaved: (saved: Budget) => Promise<void> | void;
}) {
  const [name, setName] = useState(budget?.name ?? "");
  const [amount, setAmount] = useState(budget ? String(budget.amount) : "");
  const [period, setPeriod] = useState<BudgetPeriod>(budget?.period ?? "month");
  const [currency, setCurrency] = useState(budget?.currency ?? "USD");
  const [thresholds, setThresholds] = useState((budget?.thresholds ?? [50, 80, 100]).join(", "));
  const [scope, setScope] = useState<string[]>(budget?.providerAccountIds ?? []);
  const [workspaceIds, setWorkspaceIds] = useState<string[]>(budget?.workspaceIds ?? []);
  const [vmIds, setVmIds] = useState<string[]>(budget?.vmIds ?? []);
  const toggle = (set: (update: (current: string[]) => string[]) => void, id: string, on: boolean) =>
    set(current => on ? [...current.filter(item => item !== id), id] : current.filter(item => item !== id));
  const [inApp, setInApp] = useState(budget?.notifications.inApp ?? true);
  const [webhook, setWebhook] = useState(budget?.notifications.webhook ?? false);
  const [webhookUrl, setWebhookUrl] = useState("");
  const [removeWebhook, setRemoveWebhook] = useState(false);
  const [rotate, setRotate] = useState(false);
  const [email, setEmail] = useState(budget?.notifications.email ?? false);
  const [audience, setAudience] = useState<BudgetEmailAudience>(budget?.notifications.emailAudience ?? "admins");
  const target = budget?.notifications.webhookTarget ?? null;
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault();
    const parsed = thresholds.split(",").map(x => x.trim()).filter(Boolean).map(Number);
    if (parsed.some(x => !Number.isInteger(x))) { setError("Thresholds must be whole percentages, for example 50, 80, 100."); return; }
    const url = webhookUrl.trim();
    if (webhook && !removeWebhook && !url && !target) { setError("Enter an HTTPS webhook URL or turn off webhook notifications."); return; }
    const notifications: BudgetNotificationInput = { inApp, webhook: webhook && !removeWebhook, email, emailAudience: audience };
    if (url && !removeWebhook) notifications.webhookUrl = url;
    else if (removeWebhook) notifications.webhookUrl = "";
    if (rotate && !removeWebhook) notifications.rotateWebhookSecret = true;
    // A whole selected workspace already includes its VMs.
    const selectedVms = vmIds.filter(id => !workspaces.some(item => workspaceIds.includes(item.id) && item.vms.some(vm => vm.id === id)));
    const input = { name, amount: Number(amount), period, thresholds: parsed, providerAccountIds: scope, workspaceIds,
      vmIds: selectedVms, notifications };
    setSaving(true);
    setError(null);
    try {
      const saved = budget
        ? await updateBudget(budget.id, { ...input, revision: budget.revision })
        : await createBudget({ ...input, organizationId, currency });
      await onSaved(saved);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Budget could not be saved.");
    } finally { setSaving(false); }
  }
  return <Card>
    <CardHeader><CardTitle>{budget ? `Edit ${budget.name}` : "Create budget"}</CardTitle>
      <CardDescription>Alerts are informational: they are checked in the background every few minutes, sent once per threshold per budget period, and never block or stop workspaces.</CardDescription>
    </CardHeader>
    <CardContent>
      <form className="grid gap-4" onSubmit={event => void submit(event)}>
        <div className="grid gap-2"><Label htmlFor="budget-name">Name</Label>
          <Input id="budget-name" value={name} maxLength={128} required onChange={event => setName(event.target.value)} /></div>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="grid gap-2"><Label htmlFor="budget-period">Period</Label>
            <select id="budget-period" className={selectClass} value={period} onChange={event => setPeriod(event.target.value as BudgetPeriod)}>
              {(Object.keys(periodLabels) as BudgetPeriod[]).map(value => <option key={value} value={value}>{periodLabels[value]}</option>)}
            </select>
            <p className="text-xs text-muted-foreground">UTC calendar months, ISO weeks (Monday to Sunday) or calendar quarters.</p></div>
          <div className="grid gap-2"><Label htmlFor="budget-amount">Amount per {periodNouns[period]}</Label>
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
        {workspaces.length > 0 && <fieldset className="grid gap-2"><legend className="text-sm font-medium">Workspaces and VMs</legend>
          <p className="text-xs text-muted-foreground">Leave all unchecked to include every workspace in the selected provider accounts.
            A workspace includes its VMs and storage; a single VM includes only its compute.</p>
          {workspaces.map(workspace => {
            const whole = workspaceIds.includes(workspace.id);
            return <div key={workspace.id} className="grid gap-1">
              <label className="flex items-center gap-2 text-sm">
                <Checkbox aria-label={`Workspace ${workspace.name}`} checked={whole}
                  onCheckedChange={checked => toggle(setWorkspaceIds, workspace.id, checked === true)} />
                {workspace.name} <span className="text-muted-foreground">({workspace.region})</span>
              </label>
              {workspace.vms.length > 0 && <div className="grid gap-1 pl-6">{workspace.vms.map(vm =>
                <label key={vm.id} className="flex items-center gap-2 text-sm">
                  <Checkbox aria-label={`VM ${workspace.name} ${vm.kind} ${vm.sku}`} disabled={whole} checked={whole || vmIds.includes(vm.id)}
                    onCheckedChange={checked => toggle(setVmIds, vm.id, checked === true)} />
                  {vm.kind} VM · {vm.sku} <span className="text-muted-foreground">({vm.desiredState})</span>
                </label>)}</div>}
            </div>;
          })}
        </fieldset>}
        <fieldset className="grid gap-2"><legend className="text-sm font-medium">Notifications</legend>
          <label className="flex items-center gap-2 text-sm">
            <Checkbox aria-label="In-app alerts" checked={inApp} onCheckedChange={checked => setInApp(checked === true)} />
            In-app alerts (dashboard banner and activity log)
          </label>
          <label className="flex items-center gap-2 text-sm">
            <Checkbox aria-label="Signed webhook" checked={webhook && !removeWebhook} disabled={removeWebhook}
              onCheckedChange={checked => setWebhook(checked === true)} />
            Signed webhook
          </label>
          {webhook && !removeWebhook && <div className="grid gap-2 pl-6"><Label htmlFor="budget-webhook-url">Webhook URL</Label>
            <Input id="budget-webhook-url" type="url" inputMode="url" maxLength={2048} value={webhookUrl}
              placeholder={target ? `Sending to ${target}; leave blank to keep the current URL` : "https://hooks.example.com/budgets"}
              onChange={event => setWebhookUrl(event.target.value)} />
            <p className="text-xs text-muted-foreground">Public HTTPS only. Each delivery is signed with HMAC-SHA256; a new signing secret is shown once after saving.</p>
          </div>}
          {budget?.notifications.webhookSecretConfigured && <div className="grid gap-2 pl-6">
            {!removeWebhook && <label className="flex items-center gap-2 text-sm">
              <Checkbox aria-label="Rotate signing secret" checked={rotate} onCheckedChange={checked => setRotate(checked === true)} />
              Rotate signing secret
            </label>}
            <label className="flex items-center gap-2 text-sm">
              <Checkbox aria-label="Remove webhook" checked={removeWebhook} onCheckedChange={checked => setRemoveWebhook(checked === true)} />
              Remove webhook URL and signing secret
            </label>
          </div>}
          <label className="flex items-center gap-2 text-sm">
            <Checkbox aria-label="Email" checked={email} onCheckedChange={checked => setEmail(checked === true)} />
            Email
          </label>
          {email && <div role="radiogroup" aria-label="Email recipients" className="grid gap-1 pl-6 text-sm">
            {(["admins", "members"] as const).map(value => <label key={value} className="flex items-center gap-2">
              <input type="radio" name="budget-email-audience" value={value} checked={audience === value}
                onChange={() => setAudience(value)} />
              {value === "admins" ? "Owners and admins" : "All organization members"}
            </label>)}
            <p className="text-xs text-muted-foreground">Sent to each member's account email address; other addresses cannot be added.</p>
          </div>}
          {email && budget && !budget.notifications.emailAvailable && <p className="text-xs text-muted-foreground">
            Outbound email is not configured for this installation, so no email is sent until an operator sets it up.
          </p>}
        </fieldset>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>{budget ? "Save budget" : "Create budget"}</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>Cancel</Button>
        </div>
      </form>
    </CardContent>
  </Card>;
}
