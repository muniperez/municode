import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, expect, it, vi } from "vitest";

const apiRequest = vi.hoisted(() => vi.fn());
vi.mock("@/api/apiClient", () => ({ apiRequest }));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => ({ status: "authenticated" }) }));
afterEach(() => { cleanup(); vi.clearAllMocks(); window.history.replaceState(null, "", "/"); });

it.each([
  ["callback", "code=synthetic-code", "/api/github-apps/complete", { code: "synthetic-code" }, "app_created"],
  ["installed", "installation_id=123", "/api/github-apps/installation", { installationId: 123 }, "connected"],
])("returns the %s callback to the modal without forwarding callback credentials", async (route, query, endpoint, body, status) => {
  vi.resetModules();
  window.history.replaceState(null, "", `/settings/github/${route}?connectionId=fixture-connection&state=synthetic-state&${query}`);
  const { GitHubCallbackModule } = await import("./GitHubSettingsModule");
  expect(window.location.search).toBe("");
  apiRequest.mockResolvedValue({ organizationId: "org-fixture", status });
  render(<MemoryRouter><GitHubCallbackModule /></MemoryRouter>);
  await screen.findByText(new RegExp(`GitHub status: ${status}`));
  expect(apiRequest).toHaveBeenCalledExactlyOnceWith(endpoint, {
    method: "POST", body: { connectionId: "fixture-connection", state: "synthetic-state", ...body },
  });
  expect(screen.getByRole("link", { name: "Return to GitHub settings" }))
    .toHaveAttribute("href", "/workspaces?settings=github&organizationId=org-fixture");
  expect(localStorage.length).toBe(0);
  expect(sessionStorage.length).toBe(0);
});
