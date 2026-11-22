import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiRequest } from "@/api/apiClient";
import { GitHubSettingsModule } from "./GitHubSettingsModule";

vi.mock("@/api/apiClient", () => ({ apiRequest: vi.fn() }));
const auth = vi.hoisted(() => ({
  selectedOrganization: { id: "org", role: "owner" },
  selectOrganization: vi.fn(),
}));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
beforeEach(() => {
  vi.clearAllMocks(); window.history.replaceState(null, "", "/settings/github");
  auth.selectedOrganization = { id: "org", role: "owner" };
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "org", status: "not_configured" });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it("offers private personal and organization registration only to administrators", async () => {
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByRole("button", { name: "Set up GitHub" })).toBeInTheDocument();
  expect(screen.getByLabelText("App owner type")).toHaveValue("User");
  fireEvent.change(screen.getByLabelText("App owner type"), { target: { value: "Organization" } });
  expect(screen.getByLabelText("App owner type")).toHaveValue("Organization");
  expect(screen.getByText(/Creating an App does not install it/)).toBeInTheDocument();
  expect(screen.getByText("Only select repositories")).toBeInTheDocument();
  expect(screen.getByText(/Contents and Pull requests read\/write and mandatory Metadata read/)).toBeInTheDocument();
  expect(screen.getByText(/Workspace Git credentials/)).toBeInTheDocument();
});

it("does not offer registration to a developer", async () => {
  auth.selectedOrganization.role = "developer";
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  await screen.findByRole("status");
  expect(screen.queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
  expect(screen.getByText(/Only organization owners/)).toBeInTheDocument();
});

it("keeps app-created and installed states distinct", async () => {
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "org", status: "app_created", connectionId: "connection", appSlug: "fixture" });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByRole("button", { name: "Install App / select repositories" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Verify repository access" })).not.toBeInTheDocument();
});

it("fetches repositories from the tenant connection and clears the previous tenant on switch", async () => {
  vi.mocked(apiRequest).mockImplementation(async path => path.endsWith("/repositories")
    ? [{ id: 1, fullName: "fixture/private", private: true }]
    : { organizationId: auth.selectedOrganization.id, status: "connected", connectionId: "connection" });
  const view = render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByText("fixture/private (private)")).toBeInTheDocument();
  expect(screen.getByText("GitHub connected")).toBeInTheDocument();
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/connection/repositories", expect.objectContaining({ signal: expect.any(AbortSignal) }));
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "other", status: "not_configured" });
  auth.selectedOrganization = { id: "other", role: "owner" };
  view.rerender(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  await waitFor(() => expect(screen.queryByText("fixture/private (private)")).not.toBeInTheDocument());
});

it("discards a cancelled pending attempt explicitly before submitting a fresh manifest", async () => {
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  const submit = vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(() => {});
  const pending = { organizationId: "org", status: "setup_pending", connectionId: "old" };
  vi.mocked(apiRequest).mockResolvedValue(pending);
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  const discard = await screen.findByRole("button", { name: "Discard pending attempt" });
  expect(screen.getByText(/registration pending/i)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
  vi.mocked(apiRequest).mockImplementation(async (path, options) => {
    if (path === "/api/github-apps/setup") return {
      connectionId: "fresh", postUrl: "https://github.com/settings/apps/new?state=fresh-state",
      manifest: { name: "Fresh App", public: false },
    };
    if (options?.method) return undefined;
    return { organizationId: "org", status: "not_configured" };
  });
  fireEvent.click(discard);
  const setup = await screen.findByRole("button", { name: "Set up GitHub" });
  expect(confirm).toHaveBeenCalledWith(expect.stringContaining("no App was created"));
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/old/disconnect", { method: "POST" });
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/old", { method: "DELETE" });
  expect(apiRequest).not.toHaveBeenCalledWith("/api/github-apps/setup", expect.anything());
  fireEvent.change(screen.getByLabelText("GitHub owner login"), { target: { value: "fixture" } });
  fireEvent.click(setup);
  await waitFor(() => expect(submit).toHaveBeenCalledOnce());
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/setup", {
    method: "POST", body: { organizationId: "org", ownerLogin: "fixture", ownerType: "User" },
  });
  const form = document.querySelector<HTMLFormElement>('form[action^="https://github.com/"]')!;
  expect(form.action).toBe("https://github.com/settings/apps/new?state=fresh-state");
  expect(form.querySelector<HTMLInputElement>('input[name="manifest"]')?.value).toBe('{"name":"Fresh App","public":false}');
  form.remove();
});

it("does not discard a pending attempt without confirmation", async () => {
  vi.spyOn(window, "confirm").mockReturnValue(false);
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "org", status: "setup_pending", connectionId: "connection" });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  fireEvent.click(await screen.findByRole("button", { name: "Discard pending attempt" }));
  expect(apiRequest).toHaveBeenCalledTimes(1);
});

it("surfaces failed pending-attempt removal and reads its recoverable state without starting setup", async () => {
  vi.spyOn(window, "confirm").mockReturnValue(true);
  let status = "setup_pending";
  vi.mocked(apiRequest).mockImplementation(async (_path, options) => {
    if (options?.method === "POST") { status = "disconnected"; return; }
    if (options?.method === "DELETE") { status = "removing"; throw new Error("Stored credentials are unavailable."); }
    return { organizationId: "org", status, connectionId: "connection" };
  });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  fireEvent.click(await screen.findByRole("button", { name: "Discard pending attempt" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Stored credentials are unavailable.");
  expect(screen.getByRole("status")).toHaveTextContent("removing");
  expect(screen.getByRole("button", { name: "Remove stored credentials / replace App" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
});

it("does not offer a fresh registration when App conversion is uncertain", async () => {
  vi.mocked(apiRequest).mockResolvedValue({
    organizationId: "org", status: "converting", connectionId: "connection", failureCode: "github_conversion_uncertain",
  });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByText(/Do not create another App/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Discard pending attempt" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
});

it("refreshes connection status after repository verification fails instead of keeping a connected success", async () => {
  let verified = false;
  vi.mocked(apiRequest).mockImplementation(async path => {
    if (path.endsWith("/repositories")) { verified = true; throw new Error("Installation access was revoked."); }
    return { organizationId: "org", status: verified ? "revoked" : "connected", connectionId: "connection" };
  });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByRole("alert")).toHaveTextContent("Installation access was revoked.");
  expect(screen.getByRole("status")).toHaveTextContent("revoked");
  expect(screen.queryByText("GitHub connected")).not.toBeInTheDocument();
  expect(screen.queryByRole("list", { name: "Accessible repositories" })).not.toBeInTheDocument();
});
