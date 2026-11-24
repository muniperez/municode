import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import { accountCapabilities, accountSession, authFixture, browserFlow, jsonResponse } from "../../test/accountFixtures";
import { setCsrfToken } from "../../api/apiClient";
import type { BrowserFlow, FlowNode } from "../../api/accountApi";
import { AccountFlowForm } from "./AccountFlowForm";
import { base64UrlToBuffer, bufferToBase64Url } from "./passkeys";

const challenge = JSON.stringify({ publicKey: {
  challenge: "Y2hhbGxlbmdl", rpId: "dashboard.example.test", userVerification: "preferred",
  allowCredentials: [{ id: "AQI", type: "public-key" }]
} });
const createData = JSON.stringify({ publicKey: {
  rp: { name: "Municode", id: "dashboard.example.test" }, user: { id: "dXNlcg", name: "operator@example.test", displayName: "operator@example.test" },
  challenge: "Y3JlYXRl", pubKeyCredParams: [{ type: "public-key", alg: -7 }], excludeCredentials: [{ id: "AQI", type: "public-key" }]
} });
const bytes = (...values: number[]) => Uint8Array.from(values).buffer;
const text = (value: ArrayBuffer | ArrayBufferView) => new TextDecoder().decode(value as ArrayBuffer);
const node = (name: string, value: string | null, extra: Partial<FlowNode> = {}): FlowNode =>
  ({ group: "passkey", type: "hidden", name, value, required: false, messages: [], ...extra });

function loginFlow(): BrowserFlow {
  const flow = browserFlow();
  flow.ui.nodes.push(node("passkey_challenge", challenge));
  return flow;
}

function settingsFlow(passkeys: FlowNode[] = []): BrowserFlow {
  const flow = browserFlow("settings");
  flow.ui.nodes = flow.ui.nodes.filter(item => item.name !== "identifier");
  flow.ui.nodes.push(...passkeys, node("passkey_create_data", createData));
  return flow;
}

const credentials = { get: vi.fn(), create: vi.fn() };
beforeEach(() => {
  vi.stubGlobal("PublicKeyCredential", function PublicKeyCredential() {});
  Object.defineProperty(navigator, "credentials", { value: credentials, configurable: true });
  credentials.get.mockReset().mockResolvedValue({
    id: "AQI", rawId: bytes(1, 2), type: "public-key",
    response: { authenticatorData: bytes(3), clientDataJSON: bytes(4), signature: bytes(5), userHandle: bytes(6) }
  });
  credentials.create.mockReset().mockResolvedValue({
    id: "BwgJ", rawId: bytes(7, 8, 9), type: "public-key",
    response: { attestationObject: bytes(10), clientDataJSON: bytes(11) }
  });
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  setCsrfToken(null);
  Reflect.deleteProperty(navigator, "credentials");
});

function mount(kind: "login" | "settings", returnTo?: string, passkey = true) {
  const auth = authFixture({ capabilities: { ...accountCapabilities, keepSignedIn: true, passkey } });
  render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind={kind} returnTo={returnTo} /></MemoryRouter></AuthContext.Provider>);
  return auth;
}

const posts = (fetcher: ReturnType<typeof vi.fn>) => fetcher.mock.calls.filter(call => call[1].method === "POST");

describe("passkey sign-in", () => {
  it("signs in with a passkey and keeps the CLI approval return path", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: loginFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("login", "/cli/login?session=cli_1");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with passkey" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());

    expect(fetcher.mock.calls[0][0]).toContain("/browser?returnTo=%2Fcli%2Flogin%3Fsession%3Dcli_1");
    const { publicKey } = credentials.get.mock.calls[0][0];
    expect(text(publicKey.challenge)).toBe("challenge");
    expect(publicKey.rpId).toBe("dashboard.example.test");
    expect(new Uint8Array(publicKey.allowCredentials[0].id)).toEqual(Uint8Array.of(1, 2));
    const [url, request] = fetcher.mock.calls[1];
    expect(url).toContain(`/api/account/flows/login?flow=${loginFlow().id}&keepSignedIn=false`);
    const body = JSON.parse(request.body);
    expect(Object.keys(body).sort()).toEqual(["csrf_token", "method", "passkey_login"]);
    expect(body).toMatchObject({ csrf_token: "upstream-csrf", method: "passkey" });
    expect(JSON.parse(body.passkey_login)).toEqual({
      id: "AQI", rawId: "AQI", type: "public-key",
      response: { authenticatorData: "Aw", clientDataJSON: "BA", signature: "BQ", userHandle: "Bg" }
    });
    expect(await screen.findByRole("status")).toHaveTextContent("Account verified.");
  });

  it("keeps password sign-in available when the passkey prompt is canceled", async () => {
    credentials.get.mockRejectedValueOnce(new DOMException("The operation either timed out or was not allowed.", "NotAllowedError"));
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: loginFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("login");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with passkey" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Passkey sign-in was canceled. Try again or sign in with your password.");
    expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Sign in with passkey" })).toBeEnabled();
    expect(posts(fetcher)).toHaveLength(0);
    expect(auth.refreshSession).not.toHaveBeenCalled();
  });

  it("reloads the form after a rejected passkey without replaying it", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: loginFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_invalid", message: "upstream diagnostic" } }, 400))
      .mockResolvedValueOnce(jsonResponse({ flow: loginFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount("login");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with passkey" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Check your details");
    await waitFor(() => expect(screen.getByRole("button", { name: "Sign in with passkey" })).toBeEnabled());
    expect(fetcher.mock.calls[2][0]).toContain(`/api/account/flows/login?id=${loginFlow().id}`);
    expect(posts(fetcher)).toHaveLength(1);
  });

  it.each([
    ["the operator disabled passkeys", false, true],
    ["the browser has no WebAuthn", true, false]
  ])("is not offered when %s", async (_, capability, browserSupport) => {
    if (!browserSupport) vi.stubGlobal("PublicKeyCredential", undefined);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: loginFlow(), session: null })));
    mount("login", undefined, capability);
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Sign in with passkey" })).not.toBeInTheDocument();
  });
});

describe("passkey settings", () => {
  const remove = (value: string, passkey: FlowNode["passkey"], disabled = false) =>
    node("passkey_remove", value, { type: "submit", passkey, disabled });

  it("lists passkeys and enrolls a new one before reloading the list", async () => {
    setCsrfToken("synthetic-csrf");
    const enrolled = settingsFlow([remove("0a1b", { displayName: null, addedAt: "2026-11-20T10:00:00Z" }, true)]);
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: settingsFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: enrolled, session: null }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("settings", "/settings/account");
    expect(await screen.findByText("No passkeys yet.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save password" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Add passkey" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Passkey added.");
    const removeButton = await screen.findByRole("button", { name: "Remove Passkey" });
    expect(removeButton).toBeDisabled();
    expect(screen.getByText(/^Added /)).toBeInTheDocument();
    expect(screen.getByText("Set a password or add another passkey before removing this one.")).toBeInTheDocument();
    expect(auth.refreshSession).toHaveBeenCalledOnce();
    const { publicKey } = credentials.create.mock.calls[0][0];
    expect(text(publicKey.challenge)).toBe("create");
    expect(text(publicKey.user.id)).toBe("user");
    expect(new Uint8Array(publicKey.excludeCredentials[0].id)).toEqual(Uint8Array.of(1, 2));
    const [url, request] = fetcher.mock.calls[1];
    expect(url).toContain(`/api/account/flows/settings?flow=${enrolled.id}`);
    expect(url).not.toContain("keepSignedIn");
    expect(request.headers["X-Workspaces-CSRF"] ?? new Headers(request.headers).get("X-Workspaces-CSRF")).toBe("synthetic-csrf");
    const body = JSON.parse(request.body);
    expect(body).toMatchObject({ csrf_token: "upstream-csrf", method: "passkey" });
    expect(JSON.parse(body.passkey_settings_register)).toEqual({
      id: "BwgJ", rawId: "BwgJ", type: "public-key", response: { attestationObject: "Cg", clientDataJSON: "Cw" }
    });
    expect(fetcher.mock.calls[2][0]).toContain("/api/account/flows/settings/browser?returnTo=%2Fsettings%2Faccount");
  });

  it("removes a passkey by its credential id", async () => {
    setCsrfToken("synthetic-csrf");
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({
      flow: settingsFlow([remove("0a1b", { displayName: "Work laptop", addedAt: null }), remove("ff00", { displayName: null, addedAt: null })]), session: null
    })).mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: settingsFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount("settings");
    fireEvent.click(await screen.findByRole("button", { name: "Remove Work laptop" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Passkey removed.");
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ csrf_token: "upstream-csrf", method: "passkey", passkey_remove: "0a1b" });
    expect(credentials.create).not.toHaveBeenCalled();
    expect(await screen.findByText("No passkeys yet.")).toBeInTheDocument();
  });

  it("asks the user to confirm it's them with a passkey when the session is no longer privileged", async () => {
    setCsrfToken("synthetic-csrf");
    const refreshLogin = loginFlow();
    refreshLogin.id = "22222222-3333-4444-8555-666666666666";
    refreshLogin.ui.action = `/api/account/flows/login?flow=${refreshLogin.id}`;
    refreshLogin.ui.nodes[1] = { ...refreshLogin.ui.nodes[1], type: "hidden", value: "operator@example.test" };
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: settingsFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "session_refresh_required", message: "private diagnostic" } }, 403))
      .mockResolvedValueOnce(jsonResponse({ flow: refreshLogin, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: settingsFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("settings", "/settings/account");
    fireEvent.click(await screen.findByRole("button", { name: "Add passkey" }));

    expect(await screen.findByText("Confirm it's you")).toBeInTheDocument();
    expect(await screen.findByLabelText("Password")).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Save password" })).not.toBeInTheDocument();
    expect(screen.queryByText("Keep me signed in on this device")).not.toBeInTheDocument();
    expect(fetcher.mock.calls[2][0]).toContain("/api/account/flows/login/browser?returnTo=%2Fsettings%2Faccount");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with passkey" }));

    expect(await screen.findByText("Identity confirmed. Repeat your change to continue.")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Add passkey" })).toBeEnabled();
    expect(fetcher.mock.calls[3][0]).toContain(`/api/account/flows/login?flow=${refreshLogin.id}`);
    expect(fetcher.mock.calls[3][0]).not.toContain("keepSignedIn");
    expect(fetcher.mock.calls[4][0]).toContain("/api/account/flows/settings/browser?");
    expect(credentials.create).toHaveBeenCalledOnce();
    expect(posts(fetcher)).toHaveLength(2);
    expect(auth.refreshSession).toHaveBeenCalledOnce();
  });
});

describe("passkey encoding", () => {
  it("round-trips unpadded base64url and rejects other alphabets", () => {
    const value = Uint8Array.of(251, 255, 0, 62);
    expect(bufferToBase64Url(value.buffer)).toBe("-_8APg");
    expect(new Uint8Array(base64UrlToBuffer("-_8APg"))).toEqual(value);
    expect(() => base64UrlToBuffer("+/8APg")).toThrow("Invalid passkey options.");
  });
});
