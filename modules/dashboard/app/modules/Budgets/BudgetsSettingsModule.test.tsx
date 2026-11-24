import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createBudget, deleteBudget, listBudgets, updateBudget, type Budget } from "@/api/budgetsApi";
import { listProviders } from "@/api/providersApi";
import { BudgetsSettingsModule } from "./BudgetsSettingsModule";
import { BudgetAlertBanner } from "./BudgetAlertBanner";

vi.mock("@/api/budgetsApi", () => ({ listBudgets: vi.fn(), createBudget: vi.fn(), updateBudget: vi.fn(), deleteBudget: vi.fn() }));
vi.mock("@/api/providersApi", () => ({ listProviders: vi.fn() }));
const organization = { id: "org", role: "owner" };
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => ({ selectedOrganization: organization }) }));
const openSettings = vi.fn();
vi.mock("@/modules/Settings/SettingsProvider", () => ({ useSettings: () => ({ openSettings }) }));

function budget(overrides: Partial<Budget["projection"]> = {}): Budget {
  return {
    id: "budget-1", organizationId: "org", name: "Team", currency: "USD", monthlyAmount: 50, thresholds: [50, 80, 100],
    providerAccountIds: [], revision: 3, createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z",
    projection: {
      period: "2026-11", estimate: true, complete: false, projectedHourly: 0.1056, projectedMonthly: 76, percentOfBudget: 152,
      crossedThreshold: 100, overBudget: true, providerChargesAvailable: false,
      providers: [{ providerAccountId: "do", providerName: "Droplets", providerType: "digital-ocean", computeMonthly: 72,
        storageMonthly: 4, projectedMonthly: 76, workspaceCount: 2, reservedVmCount: 1 }],
      contributors: [{ workspaceId: "ws", workspaceName: "API fixture", providerAccountId: "do", region: "fra1", skus: ["s-2vcpu"],
        reservedVmCount: 1, computeHourly: 0.1, computeMonthly: 72, storageMonthly: 2, projectedMonthly: 74, pricingStatus: "unknown" }],
      excluded: [], warnings: ["1 workspace(s) have missing pricing; their cost is unknown."],
      assumptions: ["Budgets only inform: crossing a threshold never blocks creating or starting workspaces and never stops resources."],
      ...overrides,
    },
  };
}

beforeEach(() => {
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
  vi.clearAllMocks();
  sessionStorage.clear();
  organization.role = "owner";
  vi.mocked(listBudgets).mockResolvedValue([budget()]);
  vi.mocked(listProviders).mockResolvedValue([{ id: "do", organizationId: "org", name: "Droplets", providerType: "digital-ocean",
    enabled: true, validationStatus: "valid", revision: 1, planCount: 1 }]);
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it("labels projections as estimates with provider breakdown, warnings and over-budget status", async () => {
  render(<BudgetsSettingsModule />);
  const card = await screen.findByLabelText("Budget Team");
  expect(within(card).getByText("Over budget")).toBeInTheDocument();
  expect(within(card).getByText("Incomplete estimate")).toBeInTheDocument();
  expect(within(card).getByText(/Estimated \$76\.00/)).toBeInTheDocument();
  expect(within(card).getByText(/Provider-reported charges are not available yet/)).toBeInTheDocument();
  expect(within(card).getByRole("table", { name: "Team estimate by provider" })).toHaveTextContent("Droplets (digital-ocean)");
  expect(within(card).getByText(/missing pricing/)).toBeInTheDocument();
  expect(screen.getByText(/Budgets only inform\. Crossing a threshold never blocks/)).toBeInTheDocument();
});

it("is read-only for viewers", async () => {
  organization.role = "viewer";
  render(<BudgetsSettingsModule />);
  await screen.findByLabelText("Budget Team");
  expect(screen.queryByRole("button", { name: "Create budget" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
  expect(screen.getByText(/Owners and admins can change them/)).toBeInTheDocument();
});

it("lets owners create, edit with the current revision and delete after confirmation", async () => {
  vi.mocked(createBudget).mockResolvedValue(budget());
  vi.mocked(updateBudget).mockResolvedValue(budget());
  vi.mocked(deleteBudget).mockResolvedValue(undefined);
  render(<BudgetsSettingsModule />);
  await screen.findByLabelText("Budget Team");
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Droplets" } });
  fireEvent.change(screen.getByLabelText("Monthly amount"), { target: { value: "25.5" } });
  fireEvent.change(screen.getByLabelText("Alert thresholds (%)"), { target: { value: "80, 100" } });
  fireEvent.click(screen.getByRole("checkbox"));
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  await waitFor(() => expect(createBudget).toHaveBeenCalledWith({ organizationId: "org", currency: "USD", name: "Droplets",
    monthlyAmount: 25.5, thresholds: [80, 100], providerAccountIds: ["do"] }));

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  fireEvent.change(screen.getByLabelText("Monthly amount"), { target: { value: "60" } });
  fireEvent.click(screen.getByRole("button", { name: "Save budget" }));
  await waitFor(() => expect(updateBudget).toHaveBeenCalledWith("budget-1", { name: "Team", monthlyAmount: 60,
    thresholds: [50, 80, 100], providerAccountIds: [], revision: 3 }));

  fireEvent.click(await screen.findByRole("button", { name: "Delete" }));
  expect(deleteBudget).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Confirm delete" }));
  await waitFor(() => expect(deleteBudget).toHaveBeenCalledWith("budget-1"));
});

it("shows a dismissible non-blocking alert banner that opens Budgets settings", async () => {
  const { unmount } = render(<BudgetAlertBanner />);
  const banner = await screen.findByRole("status");
  expect(banner).toHaveTextContent("Budget “Team” is estimated at $76.00 of $50.00 this month (152%, crossed 100%)");
  expect(banner).toHaveTextContent("nothing was blocked or stopped");
  fireEvent.click(within(banner).getByRole("button", { name: "Review budgets" }));
  expect(openSettings).toHaveBeenCalledWith("budgets", expect.any(HTMLElement));
  fireEvent.click(within(banner).getByRole("button", { name: "Dismiss" }));
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
  unmount();
  render(<BudgetAlertBanner />);
  await waitFor(() => expect(listBudgets).toHaveBeenCalledTimes(2));
  await new Promise(resolve => setTimeout(resolve, 0));
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

it("shows no banner when no threshold is crossed", async () => {
  vi.mocked(listBudgets).mockResolvedValue([budget({ crossedThreshold: null, overBudget: false, percentOfBudget: 10 })]);
  render(<BudgetAlertBanner />);
  await waitFor(() => expect(listBudgets).toHaveBeenCalled());
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
});
