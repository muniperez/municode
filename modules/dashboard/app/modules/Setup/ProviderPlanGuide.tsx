import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { listPlans, listProviders } from "../../api/providersApi";
import { useAuth } from "../../core/auth/AuthProvider";

export function ProviderPlanGuide() {
  const { selectedOrganization } = useAuth();
  const organizationId = selectedOrganization?.id;
  const [counts, setCounts] = useState<{ accounts: number; plans: number } | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [retry, setRetry] = useState(0);

  useEffect(() => {
    setCounts(null);
    setUnavailable(false);
    if (!organizationId) return;
    const controller = new AbortController();
    void Promise.all([
      listProviders(organizationId, controller.signal),
      listPlans(organizationId, controller.signal),
    ])
      .then(([accounts, plans]) => {
        if (!controller.signal.aborted) setCounts({ accounts: accounts.length, plans: plans.length });
      })
      .catch(() => {
        if (!controller.signal.aborted) setUnavailable(true);
      });
    return () => controller.abort();
  }, [organizationId, retry]);

  return (
    <section className="max-w-2xl space-y-4">
      <h1 className="text-2xl font-semibold">Provider and workspace plan guide</h1>
      <p>
        Municode reads existing provider accounts and plans here; it never creates infrastructure or
        submits credentials from this guide.
      </p>
      {!organizationId ? (
        <p>
          Select an organization, or <Link className="underline" to="/onboarding">create one</Link> if
          your authenticated account has none.
        </p>
      ) : (
        <>
          {!counts && !unavailable ? <p role="status">Checking provider accounts and plans…</p> : null}
          {unavailable ? (
            <div role="alert">
              <p>Provider information is temporarily unavailable. Existing configuration has not been changed.</p>
              <Button onClick={() => setRetry((value) => value + 1)}>Retry provider check</Button>
            </div>
          ) : null}
          {counts ? (
            <p role="status">
              {counts.accounts} provider account(s), {counts.plans} workspace plan(s) in this organization.
            </p>
          ) : null}
          <ol className="list-inside list-decimal space-y-3">
            <li>
              <Link className="underline" to="/plans/accounts">Connect a provider account</Link> on the
              provider screen and validate its credential. Setup never accepts provider credentials.
            </li>
            <li>
              <Link className="underline" to="/plans">Create a workspace plan</Link> mapped to that
              account, region, and server size.
            </li>
            <li>
              <Link className="underline" to="/workspaces">Open Workspaces</Link>, choose the plan, and
              create a development workspace. Starting a workspace can allocate billable VPS resources.
            </li>
          </ol>
        </>
      )}
      <p>Already configured? Continue to Workspaces and choose a workspace plan.</p>
      <Link className="underline" to="/workspaces">Continue to workspaces</Link>
    </section>
  );
}
