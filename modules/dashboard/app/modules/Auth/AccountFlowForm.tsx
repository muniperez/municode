import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Fragment, useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import {
  browserNavigation, getAccountFlow, keepSignedInHint, rememberKeepSignedInHint, startAccountFlow, startProviderRedirect,
  submitAccountFlow,
  type BrowserFlow, type FlowKind, type FlowNode
} from "../../api/accountApi";
import { accountErrorMessage } from "../../api/apiClient";
import { useAuth } from "../../core/auth/AuthProvider";
import { ApiError } from "../../models/controlPlane";
import { createPasskey, getPasskeyAssertion, passkeyErrorMessage, passkeysSupported } from "./passkeys";

function fieldLabel(name: string) {
  switch (name) {
    case "identifier": case "traits.email": case "email": return "Email";
    case "password": return "Password";
    case "code": return "Recovery code";
    case "traits.name": return "Display name";
    case "traits.name.first": return "First name";
    case "traits.name.last": return "Last name";
    default: return "Account detail";
  }
}

function supportedNode(node: FlowNode) {
  if (node.name === "password" && node.type !== "password" ||
      node.name === "csrf_token" && node.type !== "hidden") return false;
  return ["default", "password", "profile", "code", "recovery"].includes(node.group) &&
    ["hidden", "text", "email", "password", "submit"].includes(node.type) &&
    ["csrf_token", "identifier", "password", "method", "email", "code", "traits.email", "traits.name", "traits.name.first", "traits.name.last"].includes(node.name);
}

function expiredLoginFlow(kind: FlowKind, error: unknown) {
  return kind === "login" && error instanceof ApiError && error.code === "auth_flow_expired";
}

const passkeyNode = (flow: BrowserFlow | null, name: string) =>
  flow?.ui.nodes.find(node => node.group === "passkey" && node.type === "hidden" && node.name === name && node.value);

function passkeyAdded(value: string | null) {
  const date = value ? new Date(value) : null;
  return date && !Number.isNaN(date.getTime())
    ? `Added ${date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" })}`
    : null;
}

type PasskeyOperation = { kind: "login" } | { kind: "register" } | { kind: "remove"; node: FlowNode };

export function AccountFlowForm({ kind, returnTo = "/workspaces", flowId, notice, reauthenticate = false, onComplete }: {
  kind: FlowKind;
  returnTo?: string;
  /** Re-read this existing flow once (provider callback continuation) instead of starting a new one. */
  flowId?: string;
  notice?: string;
  /** Sign-in that refreshes the current session before a privileged settings change. */
  reauthenticate?: boolean;
  onComplete?: () => void;
}) {
  const { capabilities, refreshSession } = useAuth();
  const [flow, setFlow] = useState<BrowserFlow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [complete, setComplete] = useState(false);
  const [restartRequired, setRestartRequired] = useState(false);
  const [revision, setRevision] = useState(0);
  const [status, setStatus] = useState<string | null>(null);
  const [confirmIdentity, setConfirmIdentity] = useState(false);
  const generation = useRef(0);
  const enabled = kind === "recovery" ? capabilities?.emailRecovery === true : capabilities?.password === true;
  const offerKeepSignedIn = kind === "login" && !reauthenticate && capabilities?.keepSignedIn === true;
  const [keepSignedIn, setKeepSignedIn] = useState(keepSignedInHint);
  const keepSignedInChoice = offerKeepSignedIn ? keepSignedIn : undefined;

  useEffect(() => {
    const current = ++generation.current;
    setFlow(null);
    setComplete(false);
    setError(null);
    setRestartRequired(false);
    setConfirmIdentity(false);
    if (!enabled) return;
    setBusy(true);
    const request = flowId && revision === 0 ? getAccountFlow(kind, flowId) : startAccountFlow(kind, returnTo);
    request.catch(loadError => {
      if (current === generation.current && revision === 0 && expiredLoginFlow(kind, loadError)) {
        return startAccountFlow(kind, returnTo);
      }
      throw loadError;
    }).then(result => {
      if (current === generation.current) setFlow(result.flow);
    }).catch(loadError => {
      if (current === generation.current) {
        setError(accountErrorMessage(loadError));
        setRestartRequired(true);
      }
    }).finally(() => { if (current === generation.current) setBusy(false); });
    return () => { ++generation.current; };
  }, [enabled, kind, returnTo, revision, flowId]);

  function handleExpiredFlow() {
    if (kind === "login") {
      restartFlow();
    } else {
      setError("This form expired. Restart to try again.");
      setRestartRequired(true);
    }
  }

  function restartFlow() {
    setBusy(true);
    setRevision(value => value + 1);
  }

  async function continueWithGoogle() {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    try {
      const destination = await startProviderRedirect(flow, "google", returnTo, keepSignedInChoice);
      if (current !== generation.current) return;
      if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
      browserNavigation.assign(destination);
    } catch (providerError) {
      if (current !== generation.current) return;
      if (expiredLoginFlow(kind, providerError)) {
        restartFlow();
        return;
      }
      setError(accountErrorMessage(providerError));
      setRestartRequired(true);
      setBusy(false);
    }
  }

  /** Shared submit-error recovery; returns true while a fresh login form is loading. */
  async function recoverFromSubmitError(submitError: unknown, current: number, failedFlow: BrowserFlow) {
    if (expiredLoginFlow(kind, submitError)) {
      restartFlow();
      return true;
    }
    if (kind === "settings" && submitError instanceof ApiError && submitError.code === "session_refresh_required") {
      setConfirmIdentity(true);
      return false;
    }
    if (submitError instanceof ApiError &&
      (submitError.code === "auth_flow_invalid" || submitError.code === "auth_password_rejected")) {
      setError(accountErrorMessage(submitError));
      try {
        const result = await getAccountFlow(kind, failedFlow.id);
        if (current === generation.current) setFlow(result.flow);
      } catch (readError) {
        if (current === generation.current) {
          if (expiredLoginFlow(kind, readError)) {
            restartFlow();
            return true;
          }
          setError(accountErrorMessage(readError));
          setRestartRequired(true);
        }
      }
      return false;
    }
    setError(accountErrorMessage(submitError));
    setRestartRequired(true);
    return false;
  }

  async function signedIn() {
    if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
    setComplete(true);
    await refreshSession();
    onComplete?.();
  }

  async function submitPasskey(operation: PasskeyOperation) {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    const csrf = flow.ui.nodes.find(node => node.name === "csrf_token" && node.type === "hidden")?.value ?? "";
    setBusy(true);
    setError(null);
    setStatus(null);
    let refreshing = false;
    let fields: Record<string, string>;
    try {
      fields = { csrf_token: csrf, method: "passkey" };
      if (operation.kind === "login") fields.passkey_login = await getPasskeyAssertion(passkeyNode(flow, "passkey_challenge")?.value);
      else if (operation.kind === "register") fields.passkey_settings_register = await createPasskey(passkeyNode(flow, "passkey_create_data")?.value);
      else fields.passkey_remove = operation.node.value ?? "";
    } catch (promptError) {
      if (current === generation.current) {
        setError(passkeyErrorMessage(promptError, operation.kind === "login" ? "login" : "register"));
        setBusy(false);
      }
      return;
    }
    try {
      if (current !== generation.current) return;
      const result = await submitAccountFlow(flow, fields, operation.kind === "login" ? keepSignedInChoice : undefined);
      if (current !== generation.current) return;
      if (!result.session) {
        setFlow(result.flow);
      } else if (operation.kind === "login") {
        setFlow(null);
        await signedIn();
      } else {
        // Load a fresh settings flow so the passkey list reflects the change.
        setStatus(operation.kind === "register" ? "Passkey added." : "Passkey removed.");
        refreshing = true;
        restartFlow();
        await refreshSession();
      }
    } catch (submitError) {
      if (current === generation.current) refreshing = await recoverFromSubmitError(submitError, current, flow);
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  function identityConfirmed() {
    setConfirmIdentity(false);
    setStatus("Identity confirmed. Repeat your change to continue.");
    restartFlow();
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    const form = event.currentTarget;
    const submitter = (event.nativeEvent as SubmitEvent).submitter;
    const data = new FormData(form);
    const fields: Record<string, string> = {};
    for (const [name, value] of data) {
      if (typeof value === "string") fields[name] = value;
    }
    if (submitter instanceof HTMLButtonElement && submitter.name) fields[submitter.name] = submitter.value;
    setBusy(true);
    setError(null);
    setStatus(null);
    let refreshing = false;
    try {
      const result = await submitAccountFlow(flow, fields, keepSignedInChoice);
      if (current !== generation.current) return;
      form.reset();
      setFlow(result.flow);
      if (result.session) await signedIn();
    } catch (submitError) {
      if (current !== generation.current) return;
      form.reset();
      refreshing = await recoverFromSubmitError(submitError, current, flow);
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  const nodes = flow?.ui.nodes.filter(supportedNode) ?? [];
  const google = capabilities?.google === true && kind !== "recovery" &&
    flow?.ui.providers?.some(provider => provider.id === "google") === true;
  const passkeyEnabled = capabilities?.passkey === true && kind !== "recovery";
  const passkeyLogin = passkeyEnabled && kind === "login" && passkeysSupported() && passkeyNode(flow, "passkey_challenge") !== undefined;
  const passkeyRegister = passkeyEnabled && kind === "settings" ? passkeyNode(flow, "passkey_create_data") : undefined;
  const passkeys = passkeyEnabled && kind === "settings"
    ? flow?.ui.nodes.filter(node => node.group === "passkey" && node.type === "submit" && node.name === "passkey_remove" && node.value) ?? []
    : [];
  const passkeySettings = passkeyRegister !== undefined || passkeys.length > 0;
  const firstSubmit = nodes.findIndex(node => node.type === "submit");
  const hasSubmit = firstSubmit >= 0;
  const keepSignedInControl = offerKeepSignedIn ? (
    <label className="flex items-start gap-2 text-sm">
      {/* Unnamed so it is never sent as an upstream form field. */}
      <input type="checkbox" className="mt-1 accent-primary" checked={keepSignedIn} disabled={busy || restartRequired}
        onChange={event => setKeepSignedIn(event.target.checked)} />
      <span>Keep me signed in on this device</span>
    </label>
  ) : null;
  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>{reauthenticate ? "Confirm it's you" : kind === "login" ? "Sign in" : kind === "settings" ? "Account settings" : "Recover account"}</CardTitle>
        <CardDescription>
          {reauthenticate ? "Sign in again to change your account security settings." : kind === "settings"
            ? passkeySettings ? "Change your password and manage passkeys." : "Change your password."
            : "Access your Municode account."}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!enabled ? <p>This authentication method is not enabled. Contact the host administrator.</p> : null}
        {busy && !flow ? <p role="status">Loading account form</p> : null}
        {complete ? <p role="status">{kind === "settings" ? "Password updated." : "Account verified."}</p> : null}
        {status && !complete ? <p role="status">{status}</p> : null}
        {notice ? <p role="alert" className="text-sm text-destructive">{notice}</p> : null}
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {confirmIdentity ? (
          <AccountFlowForm kind="login" returnTo={returnTo} reauthenticate onComplete={identityConfirmed} />
        ) : null}
        {flow && !complete && !confirmIdentity && passkeyLogin ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired}
            onClick={() => void submitPasskey({ kind: "login" })}>
            Sign in with passkey
          </Button>
        ) : null}
        {flow && !complete && !confirmIdentity ? (
          <form key={flow.id} className="space-y-4" onSubmit={submit}>
            {flow.ui.messages.length > 0 ? <p role="status">Review your account details to continue.</p> : null}
            {nodes.map((node, index) => {
              const id = `account-${index}`;
              const key = `${node.group}:${node.name}:${node.type}`;
              if (node.type === "hidden") return <input key={key} type="hidden" name={node.name} value={node.value ?? ""} />;
              if (node.type === "submit") return (
                <Fragment key={key}>
                  {index === firstSubmit ? keepSignedInControl : null}
                  <Button type="submit" name={node.name} value={node.value ?? ""} disabled={busy || restartRequired}>
                    {kind === "login" ? "Sign in" : kind === "settings" ? node.value === "profile" ? "Save profile" : "Save password" : "Continue recovery"}
                  </Button>
                </Fragment>
              );
              return (
                <div key={key} className="space-y-2">
                  <Label htmlFor={id}>{fieldLabel(node.name)}</Label>
                  <Input id={id} name={node.name} type={node.name === "password" ? "password" : node.type}
                    autoComplete={node.name === "password" ? kind === "settings" ? "new-password" : "current-password" : node.name === "code" ? "one-time-code" :
                      node.name === "traits.name.first" ? "given-name" : node.name === "traits.name.last" ? "family-name" : "username"}
                    defaultValue={node.name === "password" || node.name === "code" ? "" : node.value ?? ""}
                    required={node.required} disabled={busy || restartRequired} aria-invalid={node.messages.length > 0} />
                  {node.messages.length > 0 ? <p className="text-sm text-destructive">Check this field.</p> : null}
                </div>
              );
            })}
            {!hasSubmit ? <p role="alert">This form cannot continue. Restart or contact the host administrator.</p> : null}
          </form>
        ) : null}
        {flow && !complete && !confirmIdentity && passkeySettings ? (
          <section aria-labelledby="account-passkeys" className="space-y-3 border-t pt-4">
            <h3 id="account-passkeys" className="font-medium">Passkeys</h3>
            <p className="text-sm text-muted-foreground">Sign in with this device's screen lock or security key instead of your password.</p>
            {passkeys.length === 0 ? <p className="text-sm">No passkeys yet.</p> : (
              <ul className="space-y-2">
                {passkeys.map(node => {
                  const name = node.passkey?.displayName ?? "Passkey";
                  const added = passkeyAdded(node.passkey?.addedAt ?? null);
                  return (
                    <li key={node.value} className="flex items-center justify-between gap-3 text-sm">
                      <span>
                        <span className="block">{name}</span>
                        {added ? <span className="block text-muted-foreground">{added}</span> : null}
                        {node.disabled ? <span className="block text-muted-foreground">Set a password or add another passkey before removing this one.</span> : null}
                      </span>
                      <Button type="button" variant="outline" size="sm" aria-label={`Remove ${name}`}
                        disabled={busy || restartRequired || node.disabled === true}
                        onClick={() => void submitPasskey({ kind: "remove", node })}>
                        Remove
                      </Button>
                    </li>
                  );
                })}
              </ul>
            )}
            {passkeyRegister && passkeysSupported() ? (
              <Button type="button" variant="outline" disabled={busy || restartRequired} onClick={() => void submitPasskey({ kind: "register" })}>
                Add passkey
              </Button>
            ) : passkeyRegister ? <p className="text-sm">This browser does not support passkeys.</p> : null}
          </section>
        ) : null}
        {flow && !complete && !confirmIdentity && google ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={continueWithGoogle}>
            {kind === "settings" ? "Link Google account" : "Continue with Google"}
          </Button>
        ) : null}
        {enabled && (kind !== "login" || restartRequired) ? <Button variant="outline" disabled={busy} onClick={restartFlow}>Restart form</Button> : null}
        {kind === "login" && !reauthenticate && capabilities?.emailRecovery ? <Link className="block underline" to="/recovery" state={{ from: returnTo }}>Forgot password?</Link> : null}
        {kind === "recovery" ? <Link className="block underline" to="/sign-in" state={{ from: returnTo }}>Return to sign in</Link> : null}
      </CardContent>
    </Card>
  );
}
