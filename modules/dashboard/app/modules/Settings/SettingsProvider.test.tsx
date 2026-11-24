import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { AuthContext } from "@/core/auth/AuthProvider";
import { AppShell } from "@/core/layout/AppShell";
import { WorkspacesModule } from "@/modules/Workspaces/WorkspacesModule";
import { authFixture, browserFlow, jsonResponse } from "@/test/accountFixtures";

let auth = authFixture();
let fetcher: ReturnType<typeof vi.fn>;
function Location() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}
function mount(path = "/workspaces") {
  return render(<AuthContext.Provider value={auth}><MemoryRouter initialEntries={[path]}>
    <Routes><Route element={<AppShell />}><Route path="/workspaces" element={<><Location /><WorkspacesModule /></>} /></Route></Routes>
  </MemoryRouter></AuthContext.Provider>);
}
async function openSettings() {
  await screen.findByText(/No workspaces yet\./);
  const opener = screen.getByRole("button", { name: "Settings" });
  opener.focus();
  fireEvent.click(opener);
  const dialog = await screen.findByRole("dialog", { name: "Settings" });
  await within(dialog).findByText("Account settings");
  return { opener, dialog };
}
beforeEach(() => {
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
  auth = authFixture();
  sessionStorage.clear();
  window.history.replaceState(null, "", "/workspaces");
  fetcher = vi.fn(async (input: string, init?: RequestInit) => {
    const url = new URL(input);
    if (init?.method && init.method !== "GET") throw new Error(`Unexpected mutation: ${input}`);
    if (url.pathname.startsWith("/api/account/flows/settings")) return jsonResponse({ flow: browserFlow("settings"), session: null });
    if (url.pathname === "/api/github-apps") return jsonResponse({ organizationId: "org-1", status: "not_configured" });
    if (["/api/development-workspaces", "/api/providers", "/api/provider-types", "/api/plans", "/api/api-keys"].includes(url.pathname)) return jsonResponse([]);
    throw new Error(`Unexpected request: ${input}`);
  });
  vi.stubGlobal("fetch", fetcher);
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("switches all grouped items without navigating or resetting the underlying view, and restores focus on Escape", async () => {
  mount();
  await screen.findByText("No workspaces yet.");
  const search = screen.getByRole("textbox", { name: "Search workspaces" });
  fireEvent.change(search, { target: { value: "keep my search" } });
  const { opener, dialog } = await openSettings();
  expect(within(dialog).getAllByRole("heading", { level: 2 }).map(heading => heading.textContent))
    .toEqual(["Settings", "Account", "Workspace administration", "Integrations"]);
  expect(within(dialog).getByRole("button", { name: "Account" })).toHaveFocus();
  for (const label of ["Plans", "Team", "API Keys", "GitHub"]) {
    fireEvent.click(within(dialog).getByRole("button", { name: label }));
    expect(within(dialog).getByRole("region", { name: label })).toBeInTheDocument();
    expect(screen.getByTestId("location")).toHaveTextContent(/^\/workspaces$/);
  }
  await within(dialog).findByRole("button", { name: "Set up GitHub" });
  fireEvent.keyDown(dialog, { key: "Escape" });
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(opener).toHaveFocus();
  expect(search).toHaveValue("keep my search");
  expect(fetcher.mock.calls.filter(([url]) => new URL(url).pathname === "/api/development-workspaces")).toHaveLength(1);
});

it("supports arrow-key item selection and close-button focus restoration", async () => {
  mount();
  const { opener, dialog } = await openSettings();
  fireEvent.keyDown(within(dialog).getByRole("button", { name: "Account" }), { key: "ArrowDown" });
  expect(within(dialog).getByRole("button", { name: "Plans" })).toHaveFocus();
  expect(within(dialog).getByRole("region", { name: "Plans" })).toBeInTheDocument();
  fireEvent.keyDown(within(dialog).getByRole("button", { name: "Plans" }), { key: "End" });
  expect(within(dialog).getByRole("button", { name: "GitHub" })).toHaveFocus();
  fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
  await waitFor(() => expect(opener).toHaveFocus());
});

it("opens Plans directly from the Workspaces More menu and keeps provider-account navigation inside Settings", async () => {
  mount();
  await screen.findByText("No workspaces yet.");
  const more = screen.getByRole("button", { name: "More" });
  more.focus();
  fireEvent.pointerDown(more, { button: 0, ctrlKey: false });
  fireEvent.click(await screen.findByRole("menuitem", { name: "Plans" }));
  const dialog = await screen.findByRole("dialog", { name: "Settings" });
  expect(within(dialog).getByRole("button", { name: "Plans" })).toHaveFocus();
  fireEvent.click(within(dialog).getByRole("button", { name: "Provider accounts" }));
  expect(within(dialog).getByRole("heading", { name: "Provider accounts" })).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Back to plans" }));
  expect(within(dialog).getByRole("heading", { name: "Plans" })).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Provider accounts" }));
  fireEvent.click(within(within(dialog).getByRole("navigation", { name: "Breadcrumb" })).getByRole("button", { name: "Plans" }));
  expect(within(dialog).getByRole("heading", { name: "Plans" })).toBeInTheDocument();
  expect(screen.getByTestId("location")).toHaveTextContent(/^\/workspaces$/);
  fireEvent.click(within(dialog).getByRole("button", { name: "API Keys" }));
  await within(dialog).findByText("No API keys");
  const createKey = within(dialog).getAllByRole("button", { name: "Create key" })[0];
  fireEvent.click(createKey);
  const child = await screen.findByRole("dialog", { name: "Create API key" });
  fireEvent.keyDown(child, { key: "Escape" });
  await waitFor(() => expect(createKey).toHaveFocus());
  expect(screen.getByRole("dialog", { name: "Settings" })).toBeInTheDocument();
  fireEvent.keyDown(dialog, { key: "Escape" });
  await waitFor(() => expect(more).toHaveFocus());
});

it("opens the GitHub panel from a callback return query and removes the callback return entries on close", async () => {
  mount("/workspaces?settings=github&organizationId=org-1");
  const dialog = await screen.findByRole("dialog", { name: "Settings" });
  await within(dialog).findByRole("button", { name: "Set up GitHub" });
  expect(within(dialog).getByRole("button", { name: "GitHub" })).toHaveFocus();
  fireEvent.keyDown(dialog, { key: "Escape" });
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(screen.getByTestId("location")).toHaveTextContent(/^\/workspaces$/);
  expect(document.querySelector("main")).toHaveFocus();
});

it.each(["developer", "viewer"])("preserves read-only administration for %s inside the modal", async role => {
  auth = authFixture({ selectedOrganization: { ...auth.selectedOrganization!, role } });
  mount();
  const { dialog } = await openSettings();
  fireEvent.click(within(dialog).getByRole("button", { name: "Plans" }));
  await within(dialog).findByText("No plans");
  expect(within(dialog).queryByRole("button", { name: "Add plan" })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Provider accounts" }));
  await within(dialog).findByText("No provider accounts");
  expect(within(dialog).queryByRole("button", { name: "Add account" })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "API Keys" }));
  await within(dialog).findByText("No API keys");
  expect(within(dialog).queryByRole("button", { name: "Create key" })).not.toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Team" }));
  expect(within(dialog).getByText("Your role").nextElementSibling).toHaveTextContent(role);
  fireEvent.click(within(dialog).getByRole("button", { name: "GitHub" }));
  await within(dialog).findByRole("status");
  expect(within(dialog).queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
  expect(within(dialog).getByText(/Only organization owners and administrators/)).toBeInTheDocument();
});
