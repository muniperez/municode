import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiRequest } from "@/api/apiClient";

vi.mock("@/api/apiClient", () => ({ apiRequest: vi.fn() }));
const auth = vi.hoisted(() => ({
  status: "authenticated",
  profile: null as { organizations: { id: string }[] } | null,
  selectedOrganization: { id: "org", role: "owner" },
  selectOrganization: vi.fn(),
}));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));

const connected = {
  organizationId: "org", status: "connected", connectionId: "connection",
  ownerLogin: "fixture-owner", installationId: 900,
};

beforeEach(() => {
  vi.resetModules();
  vi.clearAllMocks();
  auth.status = "authenticated";
  auth.selectedOrganization = { id: "org", role: "owner" };
  auth.profile = null;
  vi.mocked(apiRequest).mockImplementation(async path =>
    path.endsWith("/repositories") ? [{ id: 1, fullName: "fixture-owner/private", private: true }] : connected);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

async function mount(path: string) {
  window.history.replaceState(null, "", path);
  const { GitHubCallbackModule } = await import("./GitHubSettingsModule");
  render(<MemoryRouter><GitHubCallbackModule /></MemoryRouter>);
}

it("verifies an installation callback before showing the verified owner and repository scope", async () => {
  const storage = vi.spyOn(Storage.prototype, "setItem");
  await mount("/settings/github/installed?connectionId=connection&state=secret&installation_id=900");
  expect(window.location.search).toBe("");
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/installation", {
    method: "POST", body: { connectionId: "connection", state: "secret", installationId: 900 },
  });
  expect(await screen.findByText("fixture-owner/private (private)")).toBeInTheDocument();
  expect(screen.getByText("GitHub connected")).toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("fixture-owner");
  expect(screen.getByRole("link", { name: "View or manage connection" })).toHaveAttribute("href", "/workspaces?settings=github&organizationId=org");
  expect(storage).not.toHaveBeenCalled();
});

it("keeps App creation distinct from an installed connection", async () => {
  vi.mocked(apiRequest).mockResolvedValue({ ...connected, status: "app_created", installationId: undefined });
  await mount("/settings/github/callback?connectionId=connection&state=secret&code=oneusecode");
  expect(await screen.findByRole("button", { name: "Install App / select repositories" })).toBeInTheDocument();
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/complete", {
    method: "POST", body: { connectionId: "connection", state: "secret", code: "oneusecode" },
  });
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
  expect(screen.queryByText(/Installation verified with GitHub/)).not.toBeInTheDocument();
});

it.each([
  ["", "Check your GitHub connection", /do not need to register a new App/, "Open GitHub settings"],
  ["?connectionId=connection&installation_id=900", "Check your GitHub connection", /do not need to register a new App/, "Open GitHub settings"],
  ["?connectionId=connection&installation_id=900&setup_action=update", "GitHub installation updated", /saved your installation changes/, "View or manage connection"],
])(
  "shows live authenticated state without consuming an incomplete scope-change callback: %s",
  async (search, title, guidance, action) => {
    await mount(`/settings/github/installed${search}`);
    expect(await screen.findByText("fixture-owner/private (private)")).toBeInTheDocument();
    expect(screen.getByText(title)).toBeInTheDocument();
    expect(screen.getByText(guidance)).toBeInTheDocument();
    expect(screen.queryByText(/restart|start again/i)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: action })).toHaveAttribute("href", "/workspaces?settings=github");
    expect(screen.getByText("GitHub connected")).toBeInTheDocument();
    expect(apiRequest).not.toHaveBeenCalledWith("/api/github-apps/installation", expect.anything());
    expect(apiRequest).not.toHaveBeenCalledWith("/api/github-apps/setup", expect.anything());
  },
);

it("opens the organization that owns the connection after a saved installation change", async () => {
  auth.profile = { organizations: [{ id: "org" }, { id: "other" }] };
  vi.mocked(apiRequest).mockImplementation(async path => path.endsWith("organizationId=other")
    ? { ...connected, organizationId: "other" } : { organizationId: "org", status: "not_configured" });
  await mount("/settings/github/installed?connectionId=connection&installation_id=900&setup_action=update");
  expect(await screen.findByRole("link", { name: "View or manage connection" }))
    .toHaveAttribute("href", "/workspaces?settings=github&organizationId=other");
  expect(auth.selectOrganization).toHaveBeenCalledWith("other");
  expect(apiRequest).not.toHaveBeenCalledWith("/api/github-apps/installation", expect.anything());
});

it("does not claim repository verification when an installation is suspended", async () => {
  vi.mocked(apiRequest).mockResolvedValue({ ...connected, status: "suspended" });
  await mount("/settings/github/installed?connectionId=connection&state=secret&installation_id=900");
  await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("suspended"));
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
  expect(screen.queryByText(/Repository access verified|Installation verified/)).not.toBeInTheDocument();
});

it("surfaces an expired callback and offers recovery without silently registering another App", async () => {
  vi.mocked(apiRequest).mockImplementation(async (_path, options) => {
    if (options?.method === "POST") throw new Error("The callback state is invalid or expired.");
    return { ...connected, status: "setup_pending", installationId: undefined };
  });
  await mount("/settings/github/callback?connectionId=connection&state=expired&code=oneusecode");
  expect(await screen.findByRole("alert")).toHaveTextContent("invalid or expired");
  expect(await screen.findByRole("button", { name: "Discard pending attempt" })).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Open GitHub settings" })).toBeInTheDocument();
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
  expect(apiRequest).not.toHaveBeenCalledWith("/api/github-apps/setup", expect.anything());
});

it("does not substitute a successful callback for an independent connection status check", async () => {
  vi.mocked(apiRequest).mockImplementation(async (_path, options) => {
    if (options?.method === "POST") return connected;
    throw new Error("Connection status is unavailable.");
  });
  await mount("/settings/github/installed?connectionId=connection&state=secret&installation_id=900");
  expect(await screen.findByRole("alert")).toHaveTextContent("Connection status is unavailable.");
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
});

it("does not consume callbacks or read connection state without an authenticated session", async () => {
  auth.status = "unauthenticated";
  await mount("/settings/github/installed?connectionId=connection&state=secret&installation_id=900");
  expect(screen.getByRole("status")).toHaveTextContent("Sign in to Municode");
  expect(window.location.search).toBe("");
  expect(apiRequest).not.toHaveBeenCalled();
});

it.each(["callback", "installed"])("renders the %s route in the dashboard shell", async suffix => {
  window.history.replaceState(null, "", `/settings/github/${suffix}`);
  const { routes } = await import("../../routes");
  const shell = routes.find(route => route.children?.some(child => child.path === `/settings/github/${suffix}`))!;
  const callbackRoute = shell.children!.find(route => route.path === `/settings/github/${suffix}`)!;
  render(<MemoryRouter initialEntries={[`/settings/github/${suffix}`]}>
    <Routes><Route element={shell.element}>
      <Route path={callbackRoute.path} element={callbackRoute.element} />
    </Route></Routes>
  </MemoryRouter>);
  expect(await screen.findByRole("navigation", { name: "Primary" })).toBeInTheDocument();
  await waitFor(() => expect(screen.getByText("GitHub connected")).toBeInTheDocument());
});
