import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";
import { getByRole, queryByRole } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

const source = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.js"), "utf8");
const html = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.html"), "utf8");
const prompt = { url: "https://example.invalid/approve?code=SYNTHETIC-CODE", code: "SYNTHETIC-CODE" };
const idle = { operation: "idle", linked: false, desired: false };
const approved = { operation: "approval_saved_activate_required", linked: false, desired: true };
const linked = { operation: "idle", linked: true, desired: true };
const pending = { operation: "idle", activation: { state: "pending" } };
const ready = { ...linked, reachable: true, activation: { state: "linked" } };
const visibilityListeners: EventListenerOrEventListenerObject[] = [];
const element = (id: string) => document.getElementById(id)!;
const button = (name: string) => getByRole<HTMLButtonElement>(document.body, "button", { name });
const status = () => getByRole(document.body, "status");
afterEach(() => {
  for (const listener of visibilityListeners.splice(0)) document.removeEventListener("visibilitychange", listener);
  vi.restoreAllMocks();
  document.body.innerHTML = "";
});

function page(values: (object | Promise<object> | Error)[]) {
  document.body.innerHTML = html;
  const fetch = vi.fn().mockImplementation(async () => {
    const value = await values.shift();
    if (value instanceof Error) throw value;
    if (!value) throw new Error("Unexpected request");
    return { ok: true, json: async () => value };
  });
  const confirm = vi.fn(() => true);
  let interval: () => void = () => {};
  let now = 0;
  const listeners = vi.spyOn(document, "addEventListener");
  runInNewContext(source, {
    document, URL, fetch, confirm, AbortSignal,
    Date: { now: () => now },
    setInterval: (callback: () => void) => { interval = callback; },
  });
  for (const [type, listener] of listeners.mock.calls) {
    if (type === "visibilitychange") visibilityListeners.push(listener);
  }
  listeners.mockRestore();
  return { fetch, confirm, poll: () => interval(), advance: (ms: number) => { now += ms; } };
}

async function settled() {
  await vi.waitFor(() => expect(element("primary")).not.toHaveAttribute("aria-disabled"));
}

it("discloses independent access and does not poll or mutate a passive page", async () => {
  const state = page([idle]);
  await settled();
  expect(status()).toHaveTextContent("Not linked");
  expect(button("Link my T3 account")).toBeVisible();
  expect(element("access")).toHaveTextContent("shell and provider-management access outside Workspaces's session controls");
  expect(element("access")).toHaveTextContent("Removing Workspaces access does not revoke the T3 link.");
  expect(element("access-details").querySelector("summary")).toBeVisible();
  expect(element("management")).toHaveAttribute("hidden");
  state.poll();
  state.confirm.mockReturnValueOnce(false);
  button("Link my T3 account").click();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  expect(state.fetch).toHaveBeenCalledWith("/status", expect.objectContaining({ method: "GET" }));
});

it("shows the private approval only while pending and requires a fresh opt-in after expiry", async () => {
  const state = page([
    idle,
    { operation: "awaiting_approval" },
    { operation: "awaiting_approval", approval: prompt },
    { operation: "approval_expired", approval: prompt },
    { operation: "awaiting_approval" },
  ]);
  await settled();
  button("Link my T3 account").click();
  await settled();
  expect(status()).toHaveTextContent("Preparing account approval");
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Waiting for account approval");
  expect(status()).not.toHaveTextContent(prompt.code);
  expect(element("code")).toHaveTextContent(prompt.code);
  const approvalLink = getByRole(document.body, "link", { name: "Open T3 account approval" });
  expect(approvalLink).toHaveAttribute("href", prompt.url);
  expect(approvalLink).toHaveAttribute("rel", "noopener noreferrer");
  approvalLink.focus();
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Approval expired");
  expect(element("code")).toBeEmptyDOMElement();
  expect(approvalLink).not.toHaveAttribute("href");
  expect(button("Start approval again")).toHaveFocus();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(4);
  button("Start approval again").click();
  await settled();
  expect(state.fetch).toHaveBeenNthCalledWith(5, "/link", expect.objectContaining({
    method: "POST", headers: { "X-Municode-Connect": "1" }, cache: "no-store",
    credentials: "same-origin", redirect: "error",
  }));
});

it("confirms activation, prevents duplicate requests, and reconnects after transport loss", async () => {
  let loseConnection = () => {};
  const activation = new Promise<object>((_, reject) => {
    loseConnection = () => reject(new Error("Restart closed the connection"));
  });
  const state = page([
    { operation: "awaiting_approval", approval: prompt }, approved, activation,
    new Error("Management temporarily unavailable"), { ...linked, reachable: false }, { ...linked, reachable: true },
  ]);
  await settled();
  state.poll();
  await settled();
  expect(element("code")).toBeEmptyDOMElement();
  expect(status()).toHaveTextContent("Account approved");
  expect(getByRole(document.body, "list", { name: "Connection setup" }).querySelector('[aria-current="step"]'))
    .toHaveTextContent("Activate link");
  expect(element("message")).toHaveTextContent("Finish active turns and save your work before restarting T3.");
  expect(element("message")).toHaveTextContent("T3 terminals will close. The VM stays running");
  const activate = button("Activate and restart T3");
  state.confirm.mockReturnValueOnce(false);
  activate.click();
  expect(state.fetch).toHaveBeenCalledTimes(2);
  activate.click();
  activate.click();
  state.poll();
  expect(status()).toHaveTextContent("Restarting T3");
  expect(state.fetch).toHaveBeenCalledTimes(3);
  loseConnection();
  await settled();
  for (let attempt = 0; attempt < 2; attempt++) {
    state.poll();
    await settled();
    expect(status()).toHaveTextContent("Restarting T3");
  }
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(getByRole(document.body, "link", { name: "Open T3 web app" })).toBeVisible();
  expect(element("unlink")).not.toBeVisible();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(6);
});

it("bounds the observation window without resetting it on pending responses, and checks again explicitly", async () => {
  const state = page([approved, pending, pending, new Error("Offline"), pending, ready]);
  await settled();
  button("Activate and restart T3").click();
  await settled();
  state.advance(150_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.advance(150_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Link status unavailable");
  expect(element("message")).toHaveTextContent("does not mean activation failed");
  expect(button("Check again")).toBeVisible();
  state.poll();
  document.dispatchEvent(new Event("visibilitychange"));
  expect(state.fetch).toHaveBeenCalledTimes(4);
  button("Check again").click();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(state.fetch).toHaveBeenCalledTimes(6);
  expect(state.fetch.mock.calls.filter(([url]) => url === "/activate")).toHaveLength(1);
});

it("observes a durable failure without refresh after the guest's full activation deadline", async () => {
  const state = page([
    approved, pending, pending, new Error("Temporary status failure"),
    { operation: "idle", statusAvailable: false, activation: { state: "failed", reason: "link_not_provisioned" } },
  ]);
  await settled();
  button("Activate and restart T3").click();
  await settled();
  state.advance(120_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.advance(100_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.advance(20_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("T3 Connect activation failed");
  expect(button("Retry activation")).toBeVisible();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(5);
});

it("explains the allowlisted account link limit without raw diagnostics and successfully retries activation", async () => {
  const state = page([
    pending,
    { operation: "idle", statusAvailable: false, activation: {
      state: "failed", reason: "link_not_provisioned",
      diagnostics: { events: ["link_reconcile_failed", "account_link_limit", "private-event"], error: "private-error" },
    } },
    pending, ready,
  ]);
  await settled();
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("T3 Connect activation failed");
  expect(element("message")).toHaveTextContent("In T3 Connect, deregister an unused T3 environment, then retry activation here");
  expect(element("message")).toHaveTextContent("Your saved approval is kept and the VM stays running");
  expect(document.body).not.toHaveTextContent(/private-event|private-error|account_link_limit|link_reconcile_failed/);
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(2);
  button("Retry activation").click();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(state.fetch).toHaveBeenNthCalledWith(3, "/activate", expect.objectContaining({ method: "POST" }));
  expect(state.fetch.mock.calls.filter(([url]) => url === "/activate")).toHaveLength(1);
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(4);
});

it("keeps checking durable pending activation through transient fetch failures", async () => {
  const state = page([pending, new Error("Offline"), new Error("Still offline"), pending, ready]);
  await settled();
  for (let attempt = 0; attempt < 3; attempt++) {
    state.advance(3000);
    state.poll();
    await settled();
    expect(status()).toHaveTextContent("Restarting T3");
  }
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(state.fetch).toHaveBeenCalledTimes(5);
});

it("reads the terminal result promptly on visibilitychange even after a pause beyond the observation window", async () => {
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const state = page([pending, ready]);
  await settled();
  hidden.mockReturnValue(true);
  document.dispatchEvent(new Event("visibilitychange"));
  state.advance(600_000);
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  hidden.mockReturnValue(false);
  document.dispatchEvent(new Event("visibilitychange"));
  expect(state.fetch).toHaveBeenCalledTimes(2);
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  document.dispatchEvent(new Event("visibilitychange"));
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(2);
});

it("coalesces visibility changes during an in-flight status request and checks immediately after it fails", async () => {
  let failStatus = () => {};
  const inFlight = new Promise<object>((_, reject) => {
    failStatus = () => reject(new Error("Interrupted status"));
  });
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const state = page([
    pending, inFlight, { operation: "idle", activation: { state: "failed", reason: "relay_unavailable" } },
  ]);
  await settled();
  state.poll();
  hidden.mockReturnValue(true);
  document.dispatchEvent(new Event("visibilitychange"));
  state.advance(600_000);
  hidden.mockReturnValue(false);
  document.dispatchEvent(new Event("visibilitychange"));
  document.dispatchEvent(new Event("visibilitychange"));
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(2);
  failStatus();
  await settled();
  expect(state.fetch).toHaveBeenCalledTimes(3);
  expect(status()).toHaveTextContent("T3 Connect activation failed");
});

it("retains one status check on return when an in-flight fetch fails while the tab is hidden past the deadline", async () => {
  let failStatus = () => {};
  const inFlight = new Promise<object>((_, reject) => {
    failStatus = () => reject(new Error("Hidden status failure"));
  });
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const state = page([pending, inFlight, ready]);
  await settled();
  state.poll();
  hidden.mockReturnValue(true);
  state.advance(600_000);
  failStatus();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(2);
  hidden.mockReturnValue(false);
  document.dispatchEvent(new Event("visibilitychange"));
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(state.fetch).toHaveBeenCalledTimes(3);
});

it("exhausts a finite request budget even if the clock does not advance and pending never finishes", async () => {
  const state = page([pending, ...Array.from({ length: 100 }, () => pending), new Error("Still unavailable")]);
  await settled();
  for (let attempt = 0; attempt < 100; attempt++) {
    state.poll();
    await settled();
  }
  expect(status()).toHaveTextContent("Link status unavailable");
  state.poll();
  document.dispatchEvent(new Event("visibilitychange"));
  expect(state.fetch).toHaveBeenCalledTimes(101);
  button("Check again").click();
  await settled();
  expect(status()).toHaveTextContent("Link status unavailable");
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(102);
}, 10_000);

it.each(["pending", "restarting"])("restores durable %s activation after refresh without offering another restart", async (activationState) => {
  const state = page([
    { operation: "idle", activation: { state: activationState } },
    { ...linked, reachable: true, activation: { state: "linked" } },
  ]);
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  expect(queryByRole(document.body, "button", { name: "Activate and restart T3" })).toBeNull();
  expect(element("management")).not.toBeVisible();
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(state.fetch.mock.calls.every(([, options]) => options.method === "GET")).toBe(true);
});

it("restores a durable actionable failure, ignores raw diagnostics and allows one explicit retry", async () => {
  const state = page([
    { operation: "idle", statusAvailable: false, activation: {
      state: "failed", reason: "link_not_provisioned", diagnostics: { error: "private-server-error" },
    } },
    { operation: "idle", activation: { state: "pending" } },
    { operation: "idle", activation: { state: "failed", reason: "relay_unavailable" } },
  ]);
  await settled();
  expect(status()).toHaveTextContent("T3 Connect activation failed");
  expect(element("message")).toHaveTextContent("did not provision the account link");
  expect(element("message")).toHaveTextContent("saved approval is kept and the VM stays running");
  expect(document.body).not.toHaveTextContent("private-server-error");
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  button("Retry activation").click();
  await settled();
  expect(status()).toHaveTextContent("Restarting T3");
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("T3 Connect activation failed");
  expect(element("message")).toHaveTextContent("Check outbound network access");
  expect(state.fetch).toHaveBeenNthCalledWith(2, "/activate", expect.objectContaining({ method: "POST" }));
});

it("distinguishes unreachable, revoked, and failed states without rendering raw data", async () => {
  const state = page([
    { ...linked, reachable: false, vmId: "private-vm-id", environmentId: "private-environment" },
    { ...linked, revoked: true, reachable: true },
    { operation: "approval_failed", diagnostic: "private-cli-error" },
  ]);
  await settled();
  expect(status()).toHaveTextContent("Linked but not reachable");
  expect(element("troubleshooting").querySelector("summary")).toBeVisible();
  expect(queryByRole(document.body, "link", { name: "Open T3 web app" })).toBeNull();
  expect(document.body).not.toHaveTextContent(/private-vm-id|private-environment/);
  button("Check link status").click();
  await settled();
  expect(status()).toHaveTextContent("Link revoked");
  button("Link my T3 account").click();
  await settled();
  expect(status()).toHaveTextContent("Approval did not finish");
  expect(document.body).not.toHaveTextContent("private-cli-error");
});

it.each([
  [{ operation: "unlinked", remoteRemovalConfirmed: false }, "Unlinked here; remote removal unconfirmed"],
  [{ operation: "unlink_not_confirmed", remoteRemovalConfirmed: false }, "Unlink not confirmed"],
  [new Error("Unlink response lost"), "Unlink not confirmed"],
])("confirms unlink and explains account-side removal if the result is incomplete (%j)", async (result, heading) => {
  const state = page([{ ...linked, reachable: true }, result]);
  await settled();
  element("management").querySelector("summary")!.click();
  state.confirm.mockReturnValueOnce(false);
  button("Unlink T3 Connect").click();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  button("Unlink T3 Connect").click();
  await settled();
  expect(state.fetch).toHaveBeenNthCalledWith(2, "/unlink", expect.objectContaining({ method: "POST" }));
  expect(status()).toHaveTextContent(heading);
  expect(element("message")).toHaveTextContent("sign in to your T3 account and remove this machine from T3 Connect");
});
