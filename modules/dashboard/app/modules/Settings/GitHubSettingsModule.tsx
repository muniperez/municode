import { useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { apiRequest } from "@/api/apiClient";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useAuth } from "@/core/auth/AuthProvider";
import { PageScaffold } from "@/core/components/PageScaffold";

type Connection = {
  organizationId: string; status: string; connectionId?: string; ownerLogin?: string; ownerType?: string;
  appSlug?: string; installationId?: number; failureCode?: string; lastVerifiedAt?: string;
};
type Repository = { id: number; fullName: string; private: boolean };
type Manifest = { connectionId: string; postUrl: string; manifest: unknown };

function message(error: unknown) {
  return error instanceof Error ? error.message : "GitHub connection could not be updated.";
}

function githubDestination(value: string) {
  const url = new URL(value);
  if (url.origin !== "https://github.com" || url.username || url.password)
    throw new Error("Untrusted GitHub destination.");
  return url.href;
}

export function GitHubSettingsModule({ notice, requestedOrganizationId }: { notice?: ReactNode; requestedOrganizationId?: string } = {}) {
  const { selectedOrganization, selectOrganization } = useAuth();
  const [connection, setConnection] = useState<Connection | null>(null);
  const [repositories, setRepositories] = useState<Repository[] | null>(null);
  const [owner, setOwner] = useState("");
  const [ownerType, setOwnerType] = useState("User");
  const [error, setError] = useState("");
  const [mutating, setMutating] = useState(false);
  const [checkingRepositories, setCheckingRepositories] = useState(false);
  const busy = mutating || checkingRepositories;
  const organizationId = selectedOrganization?.id;
  const activeOrganization = useRef(organizationId);
  activeOrganization.current = organizationId;
  const requestedOrg = requestedOrganizationId ?? new URLSearchParams(window.location.search).get("organizationId");
  const admin = ["owner", "admin"].includes(selectedOrganization?.role.toLowerCase() ?? "");

  async function refresh() {
    if (!organizationId) return;
    const result = await apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organizationId)}`);
    if (activeOrganization.current === organizationId) setConnection(result);
  }
  useEffect(() => {
    if (requestedOrg && requestedOrg !== organizationId) {
      if (!selectOrganization(requestedOrg)) setError("The requested organization is not available to your account.");
      return;
    }
    let active = true;
    setConnection(null); setRepositories(null); setError("");
    if (organizationId) {
      void apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organizationId)}`)
        .then(value => { if (active) setConnection(value); })
        .catch(reason => { if (active) setError(message(reason)); });
    }
    return () => { active = false; };
  }, [organizationId, requestedOrg, selectOrganization]);

  async function verifyRepositories(signal?: AbortSignal) {
    if (!connection?.connectionId || !organizationId || connection.organizationId !== organizationId) return;
    setRepositories(null);
    let result: Repository[] | null = null;
    try {
      result = await apiRequest<Repository[]>(`/api/github-apps/${connection.connectionId}/repositories`, { signal });
    } finally {
      const current = await apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organizationId)}`, { signal });
      if (!signal?.aborted && activeOrganization.current === organizationId) {
        setConnection(current);
        setRepositories(current.status === "connected" && current.connectionId === connection.connectionId ? result : null);
      }
    }
  }
  useEffect(() => {
    if (connection?.status !== "connected" || connection.organizationId !== organizationId) {
      setRepositories(null); setCheckingRepositories(false); return;
    }
    const controller = new AbortController();
    setCheckingRepositories(true);
    void verifyRepositories(controller.signal)
      .catch(reason => { if (!controller.signal.aborted) setError(message(reason)); })
      .finally(() => { if (!controller.signal.aborted) setCheckingRepositories(false); });
    return () => controller.abort();
  }, [connection?.connectionId, connection?.status, organizationId]);

  async function run(action: () => Promise<void>) {
    setMutating(true); setError("");
    try { await action(); } catch (reason) { setError(message(reason)); }
    finally { setMutating(false); }
  }
  async function begin() {
    const result = await apiRequest<Manifest>("/api/github-apps/setup", {
      method: "POST", body: { organizationId, ownerLogin: owner, ownerType },
    });
    const form = document.createElement("form");
    form.method = "POST"; form.action = githubDestination(result.postUrl);
    const manifest = document.createElement("input");
    manifest.type = "hidden"; manifest.name = "manifest"; manifest.value = JSON.stringify(result.manifest);
    form.append(manifest); document.body.append(form); form.submit();
  }
  async function mutate(action: string) {
    await apiRequest(`/api/github-apps/${connection!.connectionId}/${action}`, { method: "POST" });
    setRepositories(null); await refresh();
  }

  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "GitHub" }]} title="GitHub">
    {notice}
    {connection?.status !== "connected" && <Card>
      <CardHeader>
        <CardTitle>Connect GitHub in two steps</CardTitle>
        <CardDescription>Use your own private GitHub App to choose which repositories Municode can access.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <ol className="list-inside list-decimal space-y-2">
          <li>Register your App on GitHub under your personal account or organization.
            Creating an App does not install it or grant repository access.</li>
          <li>Return here, then install the App on the same account. Choose <strong>Only select repositories</strong> on
            GitHub and select the repositories to connect.</li>
        </ol>
        <p>Requests Contents and Pull requests read/write and mandatory Metadata read.
          Organization registration requires an organization owner; enterprise policies may require approval.</p>
        <p>If GitHub shows “We didn’t find an App Manifest for your request” (often after asking you to sign in
          again), or you cancel or the attempt expires, come back to this panel. Discard the pending attempt, then submit
          a fresh setup form. Do not reload or resubmit the old GitHub page.</p>
      </CardContent>
    </Card>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    {!connection && !error && <p role="status">Loading GitHub connection...</p>}
    {connection && <Card>
      <CardHeader>
        <CardTitle>{connection.status === "connected" ? "GitHub connected" : "GitHub connection"}</CardTitle>
        <CardDescription role="status">Status: {connection.status}{connection.ownerLogin ? ` - ${connection.ownerLogin}` : ""}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {connection.status === "connected" && <p>Installation verified with GitHub.
          {connection.installationId ? ` Installation ID: ${connection.installationId}.` : ""}
          {connection.lastVerifiedAt ? ` Last verified: ${new Date(connection.lastVerifiedAt).toLocaleString()}.` : ""}</p>}
        {checkingRepositories && <p>Checking current repository access with GitHub...</p>}
        {connection.status === "app_created" && <p>App registered. Install it next and select repositories to finish connecting.</p>}
        {connection.status === "setup_pending" && <>
          <p>Registration pending. Municode has not received an App from GitHub, and repository access has not been verified.</p>
          <p>If GitHub showed “We didn’t find an App Manifest for your request”, asked you to sign in again, or you
            cancelled or the attempt expired, check your GitHub App settings for a newly created App. If none was
            created, discard this pending attempt below and submit a fresh setup form. If an App was created, do not
            register a duplicate; ask an administrator to inspect the existing setup.</p>
          {connection.ownerLogin && <a className="inline-block underline" target="_blank" rel="noreferrer"
            href={connection.ownerType === "Organization"
              ? `https://github.com/organizations/${encodeURIComponent(connection.ownerLogin)}/settings/apps`
              : "https://github.com/settings/apps"}>Check GitHub App settings</a>}
        </>}
        {connection.status === "converting" && <p>App registration is being processed or needs recovery.
          Do not create another App. Ask an administrator to inspect GitHub and the stored credentials.</p>}
        {connection.failureCode && <p>Last failure: {connection.failureCode}. Check GitHub ownership, approval and App settings, then reconcile.</p>}
        {connection.appSlug && <a className="inline-block underline" href={`https://github.com/apps/${encodeURIComponent(connection.appSlug)}`} target="_blank" rel="noreferrer">View your GitHub App</a>}
        {connection.status === "connected" && repositories !== null && <div>
          <p>Verified repository access: {repositories.length} {repositories.length === 1 ? "repository" : "repositories"}.</p>
          {repositories.length > 0 ? <ul aria-label="Accessible repositories" className="list-inside list-disc">{repositories.map(repo =>
            <li key={repo.id}>{repo.fullName}{repo.private ? " (private)" : ""}</li>)}</ul> :
            <p>No repositories are currently accessible. Review repository selection on GitHub, then verify again.</p>}
        </div>}
      </CardContent>
    </Card>}
    {admin && connection?.status === "not_configured" && <form className="space-y-3" onSubmit={event => { event.preventDefault(); void run(begin); }}>
      <label htmlFor="github-owner">GitHub owner login</label>
      <Input id="github-owner" value={owner} onChange={event => setOwner(event.target.value)} required maxLength={39} />
      <label htmlFor="github-owner-type">App owner type</label>
      <select id="github-owner-type" value={ownerType} onChange={event => setOwnerType(event.target.value)}>
        <option value="User">Personal account</option><option value="Organization">Organization</option>
      </select>
      <Button disabled={busy || Boolean(requestedOrg && requestedOrg !== organizationId)} type="submit">Set up GitHub</Button>
    </form>}
    {admin && connection?.connectionId && <div className="flex flex-wrap gap-3">
      {connection.status === "setup_pending" && <Button disabled={busy} variant="outline" onClick={() => {
        if (window.confirm("Only discard this attempt if no App was created on GitHub. This clears the pending registration so you can submit a fresh form; it does not delete a GitHub App. Continue?"))
          void run(async () => {
            try {
              await apiRequest(`/api/github-apps/${connection.connectionId}/disconnect`, { method: "POST" });
              await apiRequest(`/api/github-apps/${connection.connectionId}`, { method: "DELETE" });
            } finally { await refresh(); }
          });
      }}>Discard pending attempt</Button>}
      {["app_created", "revoked", "suspended", "repositories_unverified"].includes(connection.status) &&
        <Button disabled={busy} onClick={() => void run(async () => {
          const result = await apiRequest<{ url: string }>(`/api/github-apps/${connection.connectionId}/installation-url`, { method: "POST" });
          window.location.assign(githubDestination(result.url));
        })}>Install App / select repositories</Button>}
      {connection.installationId && connection.status !== "disconnected" &&
        <Button disabled={busy} onClick={() => void run(() => mutate("reconcile"))}>Reconcile with GitHub</Button>}
      {connection.status === "disconnected" ?
        <Button disabled={busy} onClick={() => void run(() => mutate("reconnect"))}>Reconnect existing App</Button> :
        <Button disabled={busy} variant="outline" onClick={() => void run(() => mutate("disconnect"))}>Disconnect</Button>}
      {["disconnected", "removing"].includes(connection.status) && <Button disabled={busy} variant="outline" onClick={() => {
        if (window.confirm("Remove stored App credentials? The user-owned GitHub App will NOT be deleted. Delete an unused App separately on GitHub before registering a replacement."))
          void run(async () => { await apiRequest(`/api/github-apps/${connection.connectionId}`, { method: "DELETE" }); await refresh(); });
      }}>Remove stored credentials / replace App</Button>}
    </div>}
    {!admin && <p>Only organization owners and administrators can configure this integration.</p>}
    {connection && <div className="flex flex-wrap gap-3">
      {connection.status === "connected" && <Button disabled={busy} onClick={() => void run(verifyRepositories)}>Verify repository access</Button>}
      <Button variant="outline" disabled={busy} onClick={() => void run(refresh)}>Refresh connection status</Button>
    </div>}
    <p>This connects the control plane only. Workspace Git credentials and Codex/Claude authentication are separate;
      this does not enable automatic workspace clone, push or pull-request authentication.</p>
  </PageScaffold>;
}

const callback = typeof window !== "undefined" && /^\/settings\/github\/(callback|installed)$/.test(window.location.pathname)
  ? { path: window.location.pathname, parameters: new URLSearchParams(window.location.search) } : null;
if (callback) window.history.replaceState(null, "", callback.path);

export function GitHubCallbackModule() {
  const { status, profile } = useAuth();
  const started = useRef(false);
  const [result, setResult] = useState<Connection | null>(null);
  const [error, setError] = useState("");
  const [missingContext, setMissingContext] = useState(false);
  const [connectionOrganizationId, setConnectionOrganizationId] = useState<string>();
  useEffect(() => {
    if (status !== "authenticated" || started.current) return;
    started.current = true;
    const parameters = callback?.parameters;
    const connectionId = parameters?.get("connectionId");
    const state = parameters?.get("state");
    if (!parameters || !connectionId || !state) {
      // GitHub's installation-update redirect has no setup state; find which organization owns the connection.
      const organizations = profile?.organizations ?? [];
      if (!connectionId || organizations.length < 2) { setMissingContext(true); return; }
      void Promise.all(organizations.map(organization =>
        apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organization.id)}`).catch(() => null)))
        .then(values => setConnectionOrganizationId(values.find(value => value?.connectionId === connectionId)?.organizationId))
        .finally(() => setMissingContext(true));
      return;
    }
    const installation = callback?.path.endsWith("/installed");
    void apiRequest<Connection>(installation ? "/api/github-apps/installation" : "/api/github-apps/complete", {
      method: "POST", body: installation
        ? { connectionId, state, installationId: Number(parameters.get("installation_id")) }
        : { connectionId, state, code: parameters.get("code") },
    }).then(setResult).catch(reason => setError(message(reason)));
  }, [status]);
  const verifiedOrganizationId = result?.organizationId ?? connectionOrganizationId;
  const settingsUrl = `/workspaces?${new URLSearchParams({ settings: "github", ...(verifiedOrganizationId ? { organizationId: verifiedOrganizationId } : {}) })}`;
  const installationUpdated = missingContext && callback?.parameters.get("setup_action") === "update";
  const title = error ? "GitHub callback could not be verified"
    : installationUpdated ? "GitHub installation updated"
      : missingContext ? "Check your GitHub connection"
        : result?.status === "app_created" ? "GitHub App registered" : "GitHub callback processed";
  const notice = <Card>
    <CardHeader><CardTitle>{title}</CardTitle></CardHeader>
    <CardContent className="space-y-3 text-sm">
      {error ? <>
        <p role="alert" className="text-destructive">{error}</p>
        <p>Check the current connection below before retrying. If GitHub asked you to sign in again, you cancelled, or the
          attempt expired, discard the pending attempt and submit a fresh setup form. Continue an App that was already
          registered instead of creating another one.</p>
      </> : installationUpdated ?
        <p>GitHub saved your installation changes. Municode re-checked the connection and current repository access below.
          You do not need to set up GitHub again.</p> :
        missingContext ?
          <p>No setup callback context was provided. The current connection is shown below. You do not need to register
            a new App just because this callback has no setup context.</p> :
          result?.status === "connected" ?
            <p>Municode recorded the installation. The connection and repository access below are checked independently with GitHub.</p> :
            result?.status === "app_created" ?
              <p>Step 1 of 2 is complete. Next, install the App and choose <strong>Only select repositories</strong> to grant access.</p> :
              <p>The callback was processed. Check the current connection and repository access below.</p>}
      <Button asChild><Link to={settingsUrl}>{result?.status === "connected" || installationUpdated ? "View or manage connection" : "Open GitHub settings"}</Link></Button>
    </CardContent>
  </Card>;
  if (status === "authenticated" && (result || error || missingContext))
    return <GitHubSettingsModule requestedOrganizationId={verifiedOrganizationId} notice={notice} />;
  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "GitHub" }]} title="GitHub connection">
    <p role="status">{status === "authenticated" ? "Verifying GitHub callback..." : status === "initializing" ? "Checking your Municode session..." : "Sign in to Municode, then check your GitHub connection. No callback credentials have been saved in browser storage."}</p>
    <Button asChild><Link to={settingsUrl}>Open GitHub settings</Link></Button>
  </PageScaffold>;
}
