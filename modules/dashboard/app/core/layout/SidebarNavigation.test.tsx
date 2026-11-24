import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../auth/AuthProvider";
import { SidebarNavigation } from "./SidebarNavigation";

const logout = vi.fn();

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("SidebarNavigation", () => {
  it("opens the footer user menu and signs out", async () => {
    render(
      <AuthContext.Provider
        value={{
          status: "authenticated",
          capabilities: { password: true, google: false, emailRecovery: false, bootstrapState: "ready" },
          session: null,
          logout,
          refreshSession: async () => {},
          selectOrganization: () => false,
          profile: {
            id: "user-1",
            email: "muni@example.test",
            displayName: "Example Developer",
            organizations: [],
          },
          selectedOrganization: { id: "org-1", name: "MuniVentures" } as never,
          profileError: null,
          refreshProfile: async () => {},
        }}
      >
        <MemoryRouter initialEntries={["/workspaces"]}>
          <SidebarNavigation onNavigate={() => {}} />
        </MemoryRouter>
      </AuthContext.Provider>,
    );

    expect(screen.getByRole("button", { name: /Municode Workspaces.*MuniVentures/i })).toBeInTheDocument();
    expect(screen.getByText("Platform")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Workspaces" })).toHaveAttribute("aria-current", "page");
    const settings = screen.getByRole("button", { name: "Settings" });
    const userMenu = screen.getByRole("button", { name: /example developer.*muni@example.test/i });
    expect(settings.nextElementSibling).toBe(userMenu);
    expect(settings.closest("nav")).toBeNull();
    for (const name of ["Plans", "GitHub", "API Keys", "Team"]) {
      expect(screen.queryByRole("link", { name })).not.toBeInTheDocument();
    }
    fireEvent.pointerDown(screen.getByRole("button", { name: /example developer.*muni@example.test/i }), {
      button: 0,
      ctrlKey: false,
    });
    await screen.findByRole("menuitem", { name: "Log out" });
    expect(screen.queryByRole("menuitem", { name: "Settings" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("menuitem", { name: "Log out" }));

    expect(logout).toHaveBeenCalledOnce();
  });
});
