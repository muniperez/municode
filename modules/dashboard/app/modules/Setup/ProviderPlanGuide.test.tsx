import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { ProviderPlanGuide } from "./ProviderPlanGuide";
import { AuthContext } from "../../core/auth/AuthProvider";
import { authFixture } from "../../test/accountFixtures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(hasOrganization = true) {
  render(<AuthContext.Provider value={authFixture(hasOrganization ? {} : { selectedOrganization: null })}>
    <MemoryRouter><ProviderPlanGuide /></MemoryRouter>
  </AuthContext.Provider>);
}

describe("provider and plan guidance", () => {
  it("shows the seeded plan and makes workspace creation the primary action", async () => {
    const fetcher = vi.fn().mockImplementation((url: string) => {
      const items = new URL(url).pathname === "/api/providers"
        ? [{ id: "provider_seed", name: "Default provider", providerType: "vultr", enabled: true }]
        : [{ id: "plan_seed", providerAccountId: "provider_seed", name: "Default workspace plan",
            region: "ewr", sku: "vc2-4c-8gb", enabled: true, accountEnabled: true }];
      return Promise.resolve(new Response(JSON.stringify(items), {
        headers: { "content-type": "application/json" },
      }));
    });
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByText("Default provider · Vultr")).toBeInTheDocument();
    expect(screen.getByText("Default workspace plan · ewr · vc2-4c-8gb")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create workspace" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Add another provider" })).toHaveAttribute("href", "/plans/accounts");
    expect(screen.getByRole("link", { name: "Finish" })).toHaveAttribute("href", "/workspaces");
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(fetcher.mock.calls.every(([, options]) => options.method === "GET")).toBe(true);
  });

  it("keeps unavailable provider reads separate from setup and permits explicit safe read retry", async () => {
    const fetcher = vi.fn().mockRejectedValueOnce(new Error("private provider diagnostic"))
      .mockResolvedValue(new Response("[]", { headers: { "content-type": "application/json" } }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn’t load your provider configuration.");
    expect(document.body.textContent).not.toContain("private provider");
    fetcher.mockImplementation(() => Promise.resolve(new Response("[]", { headers: { "content-type": "application/json" } })));
    fireEvent.click(screen.getByRole("button", { name: "Retry provider check" }));
    expect(await screen.findByText("Connect a provider to create a workspace.")).toBeInTheDocument();
    expect(fetcher.mock.calls.every(([, options]) => options.method === "GET")).toBe(true);
  });

  it("does not reinterpret missing organization membership as installation bootstrap", () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    mount(false);
    expect(screen.getByRole("link", { name: "create one" })).toHaveAttribute("href", "/onboarding");
    expect(screen.queryByRole("link", { name: /setup/ })).not.toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });
});
