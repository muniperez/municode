import { useEffect, useRef, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { completeSetup, getSetupStatus, setupErrorMessage, type SetupStatus } from "../../api/setupApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { ApiError } from "../../models/controlPlane";

const draftFields = [
  ["email", "Email", "email", 254],
  ["displayName", "Display name", "text", 200],
  ["organizationName", "Workspace name", "text", 200],
  ["organizationSlug", "Workspace slug", "text", 63]
] as const;

function linkedSetupToken(hash: string) {
  const value = new URLSearchParams(hash.replace(/^#/, "")).get("token")?.trim();
  return value && value.length <= 1024 ? value : null;
}

export function SetupModule() {
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const [phase, setPhase] = useState<"initializing" | "loaded" | "unavailable">("initializing");
  const [draft, setDraft] = useState({ email: "", displayName: "", organizationName: "", organizationSlug: "" });
  const [setupToken, setSetupToken] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const submitting = useRef(false);
  const linkedToken = useRef<string | null>(null);
  const { refreshSession } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();

  async function load(signal?: AbortSignal) {
    setPhase("initializing");
    setError(null);
    setSetupToken("");
    setPassword("");
    try {
      const next = await getSetupStatus(signal);
      if (signal?.aborted) return;
      setStatus(next);
      setPhase("loaded");
      if (linkedToken.current && (next.state === "required" || next.state === "in_progress")) setSetupToken(linkedToken.current);
      linkedToken.current = null;
    } catch {
      if (!signal?.aborted) setPhase("unavailable");
    }
  }

  useEffect(() => {
    if (location.hash) {
      const token = linkedSetupToken(location.hash);
      if (token) linkedToken.current = token;
      navigate({ pathname: location.pathname, search: location.search }, { replace: true, state: location.state });
    }
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, []);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (submitting.current) return;
    submitting.current = true;
    setBusy(true);
    setError(null);
    const request = { ...draft, setupToken, password };
    setSetupToken("");
    setPassword("");
    try {
      await completeSetup(request);
      setStatus({ state: "ready" });
    } catch (failure) {
      setError(setupErrorMessage(failure));
      if (failure instanceof ApiError && failure.code === "setup_already_completed") {
        setStatus({ state: "ready" });
      } else if (!(failure instanceof ApiError) || failure.status === 503) {
        setPhase("unavailable");
      }
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  async function signIn() {
    await refreshSession();
    navigate("/sign-in", { state: { from: "/getting-started" } });
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-lg flex-col justify-center gap-4 px-6 py-10">
      <h1 className="text-2xl font-semibold">First operator setup</h1>
      {phase === "initializing" ? <p role="status">Checking installation setup…</p> : null}
      {phase === "unavailable" ? <>
        <p role="status">Setup is temporarily unavailable. This does not mean a new installation is required.</p>
        <Button onClick={() => void load()}>Check status</Button>
      </> : null}
      {error ? <p role="alert">{error}</p> : null}
      {phase === "loaded" && status?.state === "blocked" ? <>
        <p role="status">Setup is blocked. Contact the host administrator to reconcile existing state. Do not delete installation data or create a replacement operator.</p>
        <Button onClick={() => void load()}>Check status</Button>
      </> : null}
      {phase === "loaded" && status?.state === "ready" ? <>
        <p role="status">Setup is complete. Sign in to create your first workspace.</p>
        <Button onClick={() => void signIn()}>Continue to sign in</Button>
      </> : null}
      {phase === "loaded" && (status?.state === "required" || status?.state === "in_progress") ? <>
        <p>Open the HTTPS setup link printed by seed, or paste the one-time token from protected host output. The link carries the token only in the URL fragment, which browsers do not send to the server; this screen removes it from the address bar as soon as it is read. Setup creates one operator and workspace, not a VPS.</p>
        {status.state === "in_progress" ? <p role="status">Setup is in progress. Retry only the original operator details to reconcile the original operation; do not start a new installation.</p> : null}
        {status.expiresAt ? <p>Token expiry: <time dateTime={status.expiresAt}>{new Date(status.expiresAt).toLocaleString()}</time>. Expired tokens must be rotated on the host.</p> : <p>The host administrator must prepare setup before this form can be completed.</p>}
        <form className="space-y-4" onSubmit={(event) => void submit(event)} autoComplete="off">
          <div>
            <Label htmlFor="setup-token">Setup token</Label>
            <Input id="setup-token" type="password" autoComplete="off" maxLength={1024} required
              disabled={busy} value={setupToken} onChange={(event) => setSetupToken(event.target.value)} />
          </div>
          {draftFields.map(([key, label, type, maxLength]) => <div key={key}>
            <Label htmlFor={`setup-${key}`}>{label}</Label>
            <Input id={`setup-${key}`} type={type} maxLength={maxLength} required disabled={busy}
              pattern={key === "organizationSlug" ? "[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?" : undefined}
              value={draft[key]} onChange={(event) => setDraft((current) => ({ ...current, [key]: event.target.value }))} />
          </div>)}
          <div>
            <Label htmlFor="setup-password">Operator password</Label>
            <Input id="setup-password" type="password" autoComplete="new-password" minLength={8} maxLength={1024} required
              disabled={busy} value={password} onChange={(event) => setPassword(event.target.value)} />
          </div>
          <p>Token and password are cleared after every submission or status check and are never saved in browser storage. Other draft fields remain only while this screen is open. Retrying status never resubmits setup.</p>
          <Button type="submit" disabled={busy}>{busy ? "Completing setup…" : "Complete setup"}</Button>
          <Button type="button" variant="outline" disabled={busy} onClick={() => void load()}>Check status</Button>
        </form>
        <p>Token rotation and password recovery require host access using the shared bootstrap command; they are not public registration or recovery endpoints.</p>
      </> : null}
    </main>
  );
}
