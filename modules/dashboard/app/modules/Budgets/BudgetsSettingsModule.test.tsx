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

function budget(overrides: Partial<Budget["projection"]> = {}, extra: Partial<Budget> = {}): Budget {
  return {
    notifications: { inApp: true, webhook: false, webhookTarget: null, webhookSecretConfigured: false, email: false, emailAudience: "admins", emailAvailable: true },
    lastAlert: null, deliveries: [],
    id: "budget-1", organizationId: "org", name: "Team", currency: "USD", monthlyAmount: 50, thresholds: [50, 80, 100],
    providerAccountIds: [], revision: 3, createdAt: "2026-11-01T00:00:00Z", updatedAt: "2026-11-01T00:00:00Z",
    projection: {
      period: "2026-11", estimate: true, complete: false, projectedHourly: 0.1056, projectedMonthly: 76, percentOfBudget: 152,
      crossedThreshold: 100, overBudget: true, providerChargesAvailable: false,
      providers: [{ providerAccountId: "do", providerName: "Droplets", providerType: "digital-ocean", computeMonthly: 72,
        storageMonthly: 4, projectedMonthly: 76, workspaceCount: 2, reservedVmCount: 1, complete: false,
        assumptions: ["Compute s-2vcpu in all regions: billed per started hour at 0.10 USD/VM-hour.",
          "Fallback rate: 1 volume(s) have no storage catalog entry, so the monthly storage rate recorded when the workspace was created is used."] }],
      contributors: [{ workspaceId: "ws", workspaceName: "API fixture", providerAccountId: "do", region: "fra1", skus: ["s-2vcpu"],
        reservedVmCount: 1, computeHourly: 0.1, computeMonthly: 72, storageMonthly: 2, projectedMonthly: 74, pricingStatus: "current",
        lines: [{ resource: "compute", resourceId: "vm", sku: "s-2vcpu", quantity: 1, source: "catalog", catalogEntryId: "droplet",
          billingModel: "hourly", currency: "USD", amount: 72, status: "current" },
        { resource: "storage", resourceId: "ws", quantity: 20, source: "fallback", billingModel: "monthly", currency: "USD", amount: 2,
          status: "current" }] }],
      excluded: [], warnings: ["1 workspace(s) have missing pricing; their cost is unknown."],
      assumptions: ["Budgets only inform: crossing a threshold never blocks creating or starting workspaces and never stops resources."],
      ...overrides,
    },
    ...extra,
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
  expect(within(card).getByText("Missing prices")).toBeInTheDocument();
  fireEvent.click(within(card).getByRole("button", { name: /Pricing assumptions by provider/ }));
  const assumptions = within(card).getByRole("region", { name: "Droplets pricing assumptions" });
  expect(assumptions).toHaveTextContent("billed per started hour at 0.10 USD/VM-hour");
  expect(assumptions).toHaveTextContent("Fallback rate: 1 volume(s)");
  fireEvent.click(within(card).getByRole("button", { name: /Contributing workspaces/ }));
  expect(within(card).getByRole("table", { name: "Team contributing workspaces" })).toHaveTextContent("Current · fallback rate");
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
  fireEvent.click(screen.getByRole("checkbox", { name: /Droplets/ }));
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  await waitFor(() => expect(createBudget).toHaveBeenCalledWith({ organizationId: "org", currency: "USD", name: "Droplets",
    monthlyAmount: 25.5, thresholds: [80, 100], providerAccountIds: ["do"], notifications: { inApp: true, webhook: false, email: false, emailAudience: "admins" } }));

  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  fireEvent.change(screen.getByLabelText("Monthly amount"), { target: { value: "60" } });
  fireEvent.click(screen.getByRole("button", { name: "Save budget" }));
  await waitFor(() => expect(updateBudget).toHaveBeenCalledWith("budget-1", { name: "Team", monthlyAmount: 60,
    thresholds: [50, 80, 100], providerAccountIds: [], revision: 3, notifications: { inApp: true, webhook: false, email: false, emailAudience: "admins" } }));

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

it("configures a signed webhook, reveals the signing secret once and shows delivery status", async () => {
  vi.mocked(createBudget).mockResolvedValue(budget({}, { name: "Hooks", webhookSigningSecret: "mcwh_secret" }));
  render(<BudgetsSettingsModule />);
  await screen.findByLabelText("Budget Team");
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Hooks" } });
  fireEvent.change(screen.getByLabelText("Monthly amount"), { target: { value: "10" } });
  fireEvent.click(screen.getByRole("checkbox", { name: "In-app alerts" }));
  fireEvent.click(screen.getByRole("checkbox", { name: "Signed webhook" }));
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  expect(await screen.findByText(/Enter an HTTPS webhook URL/)).toBeInTheDocument();
  expect(createBudget).not.toHaveBeenCalled();

  fireEvent.change(screen.getByLabelText("Webhook URL"), { target: { value: " https://hooks.example.com/b?token=t " } });
  fireEvent.click(screen.getByRole("button", { name: "Create budget" }));
  await waitFor(() => expect(createBudget).toHaveBeenCalledWith(expect.objectContaining({
    notifications: { inApp: false, webhook: true, email: false, emailAudience: "admins", webhookUrl: "https://hooks.example.com/b?token=t" } })));
  const secret = await screen.findByRole("region", { name: "Webhook signing secret" });
  expect(secret).toHaveTextContent("mcwh_secret");
  expect(secret).toHaveTextContent("not shown again");
  fireEvent.click(within(secret).getByRole("button", { name: "Done" }));
  expect(screen.queryByText("mcwh_secret")).not.toBeInTheDocument();
});

it("rotates an existing webhook secret without resending the write-only URL and lists deliveries", async () => {
  const configured = budget({}, {
    notifications: { inApp: true, webhook: true, webhookTarget: "https://hooks.example.com", webhookSecretConfigured: true, email: false, emailAudience: "admins", emailAvailable: true },
    lastAlert: { period: "2026-11", threshold: 100, alertedAt: "2026-11-20T00:00:00Z" },
    deliveries: [{ id: "n1", channel: "webhook", period: "2026-11", threshold: 100, status: "pending", attempts: 2,
      lastError: "HTTP 503", createdAt: "2026-11-20T00:00:00Z", deliveredAt: null, nextAttemptAt: "2026-11-20T00:04:00Z" }],
  });
  vi.mocked(listBudgets).mockResolvedValue([configured]);
  vi.mocked(updateBudget).mockResolvedValue({ ...configured, webhookSigningSecret: "mcwh_rotated" });
  render(<BudgetsSettingsModule />);
  const card = await screen.findByLabelText("Budget Team");
  expect(card).toHaveTextContent("Notifications: in-app, webhook to https://hooks.example.com · last alert 100% for 2026-11");
  fireEvent.click(within(card).getByText("Recent notifications (1)"));
  const deliveries = within(card).getByRole("table", { name: "Team recent notifications" });
  expect(deliveries).toHaveTextContent("Retrying");
  expect(deliveries).toHaveTextContent("HTTP 503");

  fireEvent.click(within(card).getByRole("button", { name: "Edit" }));
  expect(screen.getByLabelText("Webhook URL")).toHaveAttribute("placeholder", expect.stringContaining("https://hooks.example.com"));
  fireEvent.click(screen.getByRole("checkbox", { name: "Rotate signing secret" }));
  fireEvent.click(screen.getByRole("button", { name: "Save budget" }));
  await waitFor(() => expect(updateBudget).toHaveBeenCalledWith("budget-1", expect.objectContaining({
    notifications: { inApp: true, webhook: true, email: false, emailAudience: "admins", rotateWebhookSecret: true } })));
  expect(await screen.findByRole("region", { name: "Webhook signing secret" })).toHaveTextContent("mcwh_rotated");
});

it("shows no banner for budgets with in-app alerts turned off", async () => {
  vi.mocked(listBudgets).mockResolvedValue([budget({}, {
    notifications: { inApp: false, webhook: false, webhookTarget: null, webhookSecretConfigured: false, email: false, emailAudience: "admins", emailAvailable: true } })]);
  render(<BudgetAlertBanner />);
  await waitFor(() => expect(listBudgets).toHaveBeenCalled());
  await new Promise(resolve => setTimeout(resolve, 0));
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

it("sends budget email to a member audience and explains when the operator has not configured email", async () => {
  const unconfigured = budget({}, {
    notifications: { inApp: true, webhook: false, webhookTarget: null, webhookSecretConfigured: false,
      email: true, emailAudience: "admins", emailAvailable: false },
    deliveries: [{ id: "e1", channel: "email", period: "2026-11", threshold: 50, status: "failed", attempts: 6,
      lastError: "SMTP error (451)", createdAt: "2026-11-20T00:00:00Z", deliveredAt: null, nextAttemptAt: null }],
  });
  vi.mocked(listBudgets).mockResolvedValue([unconfigured]);
  vi.mocked(updateBudget).mockResolvedValue(unconfigured);
  render(<BudgetsSettingsModule />);
  const card = await screen.findByLabelText("Budget Team");
  expect(card).toHaveTextContent("email to owners and admins (not configured)");
  fireEvent.click(within(card).getByText("Recent notifications (1)"));
  const deliveries = within(card).getByRole("table", { name: "Team recent notifications" });
  expect(within(deliveries).getByRole("cell", { name: "Email" })).toBeInTheDocument();
  expect(deliveries).toHaveTextContent("Failed6SMTP error (451)");

  fireEvent.click(within(card).getByRole("button", { name: "Edit" }));
  expect(screen.getByText(/Outbound email is not configured/)).toBeInTheDocument();
  fireEvent.click(within(screen.getByRole("radiogroup", { name: "Email recipients" })).getByLabelText("All organization members"));
  fireEvent.click(screen.getByRole("button", { name: "Save budget" }));
  await waitFor(() => expect(updateBudget).toHaveBeenCalledWith("budget-1", expect.objectContaining({
    notifications: { inApp: true, webhook: false, email: true, emailAudience: "members" } })));
});
