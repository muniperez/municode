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
import { createBudget, deleteBudget, listBudgets, updateBudget, type Budget, type BudgetContributor, type BudgetNotificationInput } from "@/api/budgetsApi";
import { listProviders, type ProviderAccount } from "@/api/providersApi";
import { formatPrice } from "@/modules/Workspaces/WorkspaceCostEstimate";

export function budgetStatus(budget: Budget) {
  const projection = budget.projection;
  if (projection.overBudget) return { label: "Over budget", variant: "destructive" as const };
  if (projection.crossedThreshold !== null) return { label: `Crossed ${projection.crossedThreshold}%`, variant: "secondary" as const };
  return { label: "Within budget", variant: "outline" as const };
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

  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "Budgets" }]} title="Budgets"
    actions={canManage && editing === null ? <Button onClick={() => setEditing("new")}>Create budget</Button> : null}>
    <p className="text-sm text-muted-foreground">
      Budgets only inform. Crossing a threshold never blocks creating or starting workspaces and never stops resources.
      All amounts are estimates from the operator's price catalog or recorded rates, not provider invoices.
    </p>
    {!canManage && <p className="text-sm text-muted-foreground">You can view budgets. Owners and admins can change them.</p>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {canManage && editing !== null && organizationId && <BudgetForm organizationId={organizationId}
      budget={editing === "new" ? null : editing} providers={providers}
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
          <TableCell>{provider.providerName} <span className="text-muted-foreground">({provider.providerType})</span>
            {!provider.complete && <Badge variant="outline" className="ml-2">Missing prices</Badge>}</TableCell>
          <TableCell>{money(provider.computeMonthly)}</TableCell><TableCell>{money(provider.storageMonthly)}</TableCell>
          <TableCell>{money(provider.projectedMonthly)}</TableCell>
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
            <TableHead>Running VMs</TableHead><TableHead>Estimated / month</TableHead><TableHead>Pricing</TableHead></TableRow></TableHeader>
          <TableBody>{projection.contributors.map(item => <TableRow key={item.workspaceId}>
            <TableCell>{item.workspaceName}</TableCell>
            <TableCell>{item.region}{item.skus.length ? ` · ${item.skus.join(", ")}` : ""}</TableCell>
            <TableCell>{item.reservedVmCount}</TableCell>
            <TableCell>{money(item.projectedMonthly)}</TableCell>
            <TableCell>{pricingLabel(item)}</TableCell>
          </TableRow>)}</TableBody>
        </Table>
      </Disclosure>}
      {projection.excluded.length > 0 && <Disclosure label={`Excluded workspaces (${projection.excluded.length})`}>
        <ul className="list-disc space-y-1 pl-5">{projection.excluded.map(item => <li key={item.workspaceId}>{item.workspaceName}: {item.reason}</li>)}</ul>
      </Disclosure>}
      {budget.deliveries.length > 0 && <Disclosure label={`Recent notifications (${budget.deliveries.length})`}>
        <Table aria-label={`${budget.name} recent notifications`}>
          <TableHeader><TableRow><TableHead>Channel</TableHead><TableHead>Threshold</TableHead>
            <TableHead>Status</TableHead><TableHead>Attempts</TableHead><TableHead>Details</TableHead></TableRow></TableHeader>
          <TableBody>{budget.deliveries.map(item => <TableRow key={item.id}>
            <TableCell>{item.channel === "in_app" ? "In-app" : "Webhook"}</TableCell>
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

function channels(budget: Budget) {
  const enabled = [budget.notifications.inApp ? "in-app" : null,
    budget.notifications.webhook ? `webhook to ${budget.notifications.webhookTarget}` : null].filter(Boolean);
  return enabled.length ? enabled.join(", ") : "off";
}

function BudgetForm({ organizationId, budget, providers, onCancel, onSaved }: {
  organizationId: string; budget: Budget | null; providers: ProviderAccount[];
  onCancel: () => void; onSaved: (saved: Budget) => Promise<void> | void;
}) {
  const [name, setName] = useState(budget?.name ?? "");
  const [amount, setAmount] = useState(budget ? String(budget.monthlyAmount) : "");
  const [currency, setCurrency] = useState(budget?.currency ?? "USD");
  const [thresholds, setThresholds] = useState((budget?.thresholds ?? [50, 80, 100]).join(", "));
  const [scope, setScope] = useState<string[]>(budget?.providerAccountIds ?? []);
  const [inApp, setInApp] = useState(budget?.notifications.inApp ?? true);
  const [webhook, setWebhook] = useState(budget?.notifications.webhook ?? false);
  const [webhookUrl, setWebhookUrl] = useState("");
  const [removeWebhook, setRemoveWebhook] = useState(false);
  const [rotate, setRotate] = useState(false);
  const target = budget?.notifications.webhookTarget ?? null;
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  async function submit(event: FormEvent) {
    event.preventDefault();
    const parsed = thresholds.split(",").map(x => x.trim()).filter(Boolean).map(Number);
    if (parsed.some(x => !Number.isInteger(x))) { setError("Thresholds must be whole percentages, for example 50, 80, 100."); return; }
    const url = webhookUrl.trim();
    if (webhook && !removeWebhook && !url && !target) { setError("Enter an HTTPS webhook URL or turn off webhook notifications."); return; }
    const notifications: BudgetNotificationInput = { inApp, webhook: webhook && !removeWebhook };
    if (url && !removeWebhook) notifications.webhookUrl = url;
    else if (removeWebhook) notifications.webhookUrl = "";
    if (rotate && !removeWebhook) notifications.rotateWebhookSecret = true;
    const input = { name, monthlyAmount: Number(amount), thresholds: parsed, providerAccountIds: scope, notifications };
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
      <CardDescription>Alerts are informational: they are checked in the background every few minutes, sent once per threshold per month, and never block or stop workspaces.</CardDescription>
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
          <p className="text-xs text-muted-foreground">Email notifications are not available yet.</p>
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
