import { useEffect, useState } from "react";
import { ArrowRight, Boxes, Cloud, Plus } from "lucide-react";
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { listPlans, listProviders, type ProviderAccount, type ProviderPlan } from "../../api/providersApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { CreateWorkspaceDialog } from "../Workspaces/CreateWorkspaceDialog";

type ProviderConfiguration = { accounts: ProviderAccount[]; plans: ProviderPlan[] };

const providerNames: Record<string, string> = {
  vultr: "Vultr",
  hetzner: "Hetzner",
  "digital-ocean": "DigitalOcean",
};

export function ProviderPlanGuide() {
  const { selectedOrganization } = useAuth();
  if (!selectedOrganization) return (
    <section className="max-w-xl space-y-4">
      <h1 className="text-2xl font-semibold">Create your first workspace</h1>
      <p>Select an organization or <Link className="underline" to="/onboarding">create one</Link>.</p>
    </section>
  );

  return <WorkspaceSetup key={selectedOrganization.id} organizationId={selectedOrganization.id}
    role={selectedOrganization.role?.toLowerCase() ?? ""} />;
}

function WorkspaceSetup({ organizationId, role }: { organizationId: string; role: string }) {
  const navigate = useNavigate();
  const [configuration, setConfiguration] = useState<ProviderConfiguration | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [retry, setRetry] = useState(0);
  const canManageProviders = role === "owner" || role === "admin";
  const canCreateWorkspace = canManageProviders || role === "developer";

  useEffect(() => {
    const controller = new AbortController();
    void Promise.all([
      listProviders(organizationId, controller.signal),
      listPlans(organizationId, controller.signal),
    ]).then(([accounts, plans]) => {
      if (!controller.signal.aborted) setConfiguration({ accounts, plans });
    }).catch(() => {
      if (!controller.signal.aborted) setUnavailable(true);
    });
    return () => controller.abort();
  }, [organizationId, retry]);

  const enabledAccounts = configuration?.accounts.filter(account => account.enabled) ?? [];
  const enabledPlans = configuration?.plans.filter(plan => plan.enabled && plan.accountEnabled) ?? [];
  const seededPlan = enabledPlans.find(plan => plan.id === "plan_seed" && plan.providerAccountId === "provider_seed");
  const plan = seededPlan ?? enabledPlans.find(plan => enabledAccounts.some(account => account.id === plan.providerAccountId));
  const provider = enabledAccounts.find(account => account.id === plan?.providerAccountId)
    ?? enabledAccounts.find(account => account.id === "provider_seed") ?? enabledAccounts[0];

  return (
    <section className="mx-auto w-full max-w-2xl py-6 sm:py-12">
      <div className="overflow-hidden rounded-2xl border bg-card shadow-sm">
        <div className="space-y-4 p-6 sm:p-9">
          <div className="flex size-12 items-center justify-center rounded-xl bg-primary/10 text-primary">
            <Boxes className="size-6" aria-hidden="true" />
          </div>
          <div className="space-y-2">
            <h1 className="text-3xl font-semibold tracking-tight">Create your first workspace</h1>
            <p className="max-w-md text-sm leading-relaxed text-muted-foreground">
              {plan ? "Start with your configured plan. Municode will provision a coding VM for your workspace."
                : "Choose a provider and plan to launch a coding VM, or go straight to Workspaces."}
            </p>
          </div>
          <div className="rounded-xl border bg-muted/30 p-4">
            {!configuration && !unavailable && <p role="status" className="text-sm">Loading provider configuration…</p>}
            {unavailable && <div role="alert" className="space-y-2 text-sm">
              <p>Couldn’t load your provider configuration.</p>
              <Button size="sm" variant="outline" onClick={() => { setUnavailable(false); setRetry(value => value + 1); }}>
                Retry provider check
              </Button>
            </div>}
            {provider ? (
              <div className="flex items-start gap-3">
                <Cloud className="mt-0.5 size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
                <div className="space-y-1">
                  <p className="text-sm font-medium">{provider.name} · {providerNames[provider.providerType] ?? provider.providerType}</p>
                  {plan ? <p className="text-sm text-muted-foreground">{plan.name} · {plan.region} · {plan.sku}</p>
                    : <p className="text-sm text-muted-foreground">Add a workspace plan for this provider.</p>}
                </div>
              </div>
            ) : configuration && <p className="text-sm text-muted-foreground">Connect a provider to create a workspace.</p>}
          </div>
          {canManageProviders && configuration && (
            <div className="flex flex-wrap gap-2">
              <Button asChild variant="ghost" size="sm">
                <Link to="/plans/accounts"><Plus className="size-4" aria-hidden="true" />
                  {provider ? "Add another provider" : "Add a provider"}</Link>
              </Button>
              {provider && !plan && <Button asChild variant="ghost" size="sm"><Link to="/plans">Add a workspace plan</Link></Button>}
            </div>
          )}
        </div>
        <div className="flex flex-wrap items-center justify-end gap-3 border-t bg-muted/20 px-6 py-4 sm:px-9">
          <Button asChild variant={plan && canCreateWorkspace ? "outline" : "default"}>
            <Link to="/workspaces">Finish <ArrowRight className="size-4" aria-hidden="true" /></Link>
          </Button>
          {plan && <CreateWorkspaceDialog organizationId={organizationId} canManage={canCreateWorkspace}
            onCreated={workspace => navigate(`/workspaces/${encodeURIComponent(workspace.id)}`)} />}
        </div>
      </div>
    </section>
  );
}
