import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { SetupModule } from "./SetupModule";
import { completeSetup, getSetupStatus } from "../../api/setupApi";
import { AuthContext } from "../../core/auth/AuthProvider";
import { AccountStatus } from "../../core/auth/AccountStatus";
import { GuestArea } from "../../core/auth/GuestArea";
import { AuthenticatedArea } from "../../core/auth/AuthenticatedArea";
import { authFixture } from "../../test/accountFixtures";
import { ApiError } from "../../models/controlPlane";
import { routes } from "../../routes";
import { dashboardRoutes } from "../../core/routing/dashboardRoutes";

vi.mock("../../api/setupApi", async (original) => ({
  ...await original<typeof import("../../api/setupApi")>(),
  getSetupStatus: vi.fn(),
  completeSetup: vi.fn()
}));
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.restoreAllMocks(); });

function Destination() {
  const location = useLocation();
  return <p>Ordinary sign in, next: {(location.state as { from: string }).from}</p>;
}

function Address() {
  const location = useLocation();
  return <span data-testid="address">{location.pathname + location.search + location.hash}</span>;
}

function mount(entry = "/setup") {
  const auth = authFixture({ status: "setup_required", profile: null });
  render(<AuthContext.Provider value={auth}><MemoryRouter initialEntries={[entry]}>
    <Routes>
      <Route path="/setup" element={<><SetupModule /><Address /></>} />
      <Route path="/sign-in" element={<Destination />} />
    </Routes>
  </MemoryRouter></AuthContext.Provider>);
  return auth;
}

async function fill() {
  fireEvent.change(await screen.findByLabelText("Setup token"), { target: { value: "synthetic-token" } });
  for (const [label, value] of [
    ["Email", "operator@example.test"], ["Display name", "Operator"],
    ["Workspace name", "Workspace"], ["Workspace slug", "workspace"],
    ["Operator password", "synthetic-password"]
  ]) fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("first operator screen", () => {
  it("checks setup independently of ordinary auth and routes success to explicit login", async () => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "required" });
    vi.mocked(completeSetup).mockResolvedValue({ installationId: "installation", operationId: "operation", state: "ready" });
    const storage = vi.spyOn(Storage.prototype, "setItem");
    const auth = mount();
    await fill();
    fireEvent.click(screen.getByRole("button", { name: "Complete setup" }));
    expect(await screen.findByText(/No browser session was created/)).toBeInTheDocument();
    expect(completeSetup).toHaveBeenCalledOnce();
    expect(completeSetup).toHaveBeenCalledWith({
      setupToken: "synthetic-token", email: "operator@example.test", password: "synthetic-password",
      displayName: "Operator", organizationName: "Workspace", organizationSlug: "workspace"
    });
    expect(auth.refreshSession).not.toHaveBeenCalled();
    expect(storage).not.toHaveBeenCalled();
    expect(document.body.textContent).not.toContain("synthetic-token");
    expect(document.body.textContent).not.toContain("synthetic-password");
    fireEvent.click(screen.getByRole("button", { name: "Continue to sign in" }));
    expect(await screen.findByText("Ordinary sign in, next: /getting-started")).toBeInTheDocument();
    expect(auth.refreshSession).toHaveBeenCalledOnce();
  });

  it.each([
    ["setup_token_expired", 410, /token expired/],
    ["setup_token_invalid", 401, /token is invalid/],
    ["setup_in_progress", 409, /awaiting identity reconciliation/],
    ["setup_rate_limited", 429, /Wait a minute/]
  ] as const)("shows %s safely and clears secrets while retaining a memory-only draft", async (code, status, message) => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "required" });
    vi.mocked(completeSetup).mockRejectedValue(new ApiError(status, {
      code, message: "synthetic-token synthetic-password upstream", details: []
    }));
    mount();
    await fill();
    fireEvent.click(screen.getByRole("button", { name: "Complete setup" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(message);
    expect(screen.getByLabelText("Setup token")).toHaveValue("");
    expect(screen.getByLabelText("Operator password")).toHaveValue("");
    expect(screen.getByLabelText("Email")).toHaveValue("operator@example.test");
    expect(document.body.textContent).not.toContain("synthetic-password");
    expect(completeSetup).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole("button", { name: "Check status" }));
    await screen.findByLabelText("Email");
    expect(completeSetup).toHaveBeenCalledOnce();
  });

  it("does not automatically replay uncertain submissions; status retry preserves only non-secret fields", async () => {
    vi.mocked(getSetupStatus).mockResolvedValueOnce({ state: "required" }).mockResolvedValue({ state: "in_progress" });
    vi.mocked(completeSetup).mockRejectedValue(new ApiError(503, { code: "auth_unavailable", message: "private upstream", details: [] }));
    mount();
    await fill();
    fireEvent.click(screen.getByRole("button", { name: "Complete setup" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("temporarily unavailable");
    expect(screen.queryByLabelText("Setup token")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Check status" }));
    expect(await screen.findByText(/Setup is in progress/)).toBeInTheDocument();
    expect(screen.getByLabelText("Email")).toHaveValue("operator@example.test");
    expect(screen.getByLabelText("Setup token")).toHaveValue("");
    expect(screen.getByLabelText("Operator password")).toHaveValue("");
    expect(completeSetup).toHaveBeenCalledOnce();
  });

  it("handles reused tokens as ready, without providing another claim form", async () => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "required" });
    vi.mocked(completeSetup).mockRejectedValue(new ApiError(409, { code: "setup_already_completed", message: "private", details: [] }));
    mount();
    await fill();
    fireEvent.click(screen.getByRole("button", { name: "Complete setup" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("already complete");
    expect(screen.queryByLabelText("Setup token")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue to sign in" })).toBeInTheDocument();
  });

  it("prefills a setup-link token from the fragment, strips it from the address and keeps manual entry", async () => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "required" });
    vi.mocked(completeSetup).mockResolvedValue({ installationId: "installation", operationId: "operation", state: "ready" });
    mount("/setup?next=1#token=linked%2Ftoken");
    expect(screen.getByTestId("address")).toHaveTextContent(/^\/setup\?next=1$/);
    expect(await screen.findByLabelText("Setup token")).toHaveValue("linked/token");
    for (const [label, value] of [
      ["Email", "operator@example.test"], ["Display name", "Operator"],
      ["Workspace name", "Workspace"], ["Workspace slug", "workspace"],
      ["Operator password", "synthetic-password"]
    ]) fireEvent.change(screen.getByLabelText(label), { target: { value } });
    fireEvent.click(screen.getByRole("button", { name: "Complete setup" }));
    await screen.findByText(/No browser session was created/);
    expect(completeSetup).toHaveBeenCalledWith(expect.objectContaining({ setupToken: "linked/token" }));
  });

  it("does not reuse a linked token after a status check and ignores links when setup is not open", async () => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "required" });
    mount("/setup#token=linked-token");
    expect(await screen.findByLabelText("Setup token")).toHaveValue("linked-token");
    fireEvent.click(screen.getByRole("button", { name: "Check status" }));
    await waitFor(() => expect(screen.getByLabelText("Setup token")).toHaveValue(""));
    cleanup();
    vi.mocked(getSetupStatus).mockResolvedValue({ state: "ready" });
    mount("/setup#token=linked-token");
    expect(await screen.findByText(/Setup is complete/)).toBeInTheDocument();
    expect(screen.getByTestId("address")).toHaveTextContent(/^\/setup$/);
    expect(document.body.textContent).not.toContain("linked-token");
  });

  it.each(["ready", "blocked"] as const)("never renders admission for %s", async (state) => {
    vi.mocked(getSetupStatus).mockResolvedValue({ state });
    mount();
    await screen.findByRole("status");
    await waitFor(() => expect(screen.queryByText("Checking installation setup…")).not.toBeInTheDocument());
    expect(screen.queryByLabelText("Setup token")).not.toBeInTheDocument();
    expect(completeSetup).not.toHaveBeenCalled();
  });

  it("distinguishes initialization and temporary unavailability and only retries status", async () => {
    vi.mocked(getSetupStatus).mockRejectedValueOnce(new Error("synthetic-token private diagnostic"))
      .mockResolvedValue({ state: "required" });
    mount();
    expect(screen.getByRole("status")).toHaveTextContent("Checking installation");
    expect(await screen.findByText(/temporarily unavailable. This does not mean/)).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("synthetic-token");
    fireEvent.click(screen.getByRole("button", { name: "Check status" }));
    await screen.findByLabelText("Setup token");
    expect(completeSetup).not.toHaveBeenCalled();
  });
});

describe("setup navigation boundary", () => {
  it("connects setup-required navigation without rewriting signin or authenticated routes", () => {
    const auth = authFixture({ status: "setup_required", profile: null });
    const { rerender } = render(<AuthContext.Provider value={auth}><MemoryRouter><AccountStatus /></MemoryRouter></AuthContext.Provider>);
    expect(screen.getByRole("link", { name: "Complete first operator setup" })).toHaveAttribute("href", "/setup");
    rerender(<AuthContext.Provider value={authFixture()}><MemoryRouter>
      <AuthenticatedArea><p>Normal ready operator apps</p></AuthenticatedArea>
    </MemoryRouter></AuthContext.Provider>);
    expect(screen.getByText("Normal ready operator apps")).toBeInTheDocument();
    rerender(<AuthContext.Provider value={authFixture({ status: "unauthenticated", profile: null })}><MemoryRouter>
      <GuestArea><p>Ordinary sign in</p></GuestArea>
    </MemoryRouter></AuthContext.Provider>);
    expect(screen.getByText("Ordinary sign in")).toBeInTheDocument();
    expect(routes.find((route) => route.path === "/setup")?.element).toBeTruthy();
    expect(dashboardRoutes.setup.layout).toBe("standalone");
    expect(routes.flatMap((route) => route.children ?? []).find((route) => route.path === "/getting-started")?.element).toBeTruthy();
  });
});
