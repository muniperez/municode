import { describe, expect, it } from "vitest";
import { buildDashboardPath, dashboardRoutes, navigationRoutes } from "./dashboardRoutes";

describe("dashboardRoutes", () => {
  it("builds workspace paths without retaining app-hosting routes", () => {
    expect(navigationRoutes.map((route) => route.key)).toEqual(["workspaces", "plans"]);
    expect(dashboardRoutes).not.toHaveProperty("apps");
    expect(dashboardRoutes).not.toHaveProperty("deployments");
    expect(dashboardRoutes).not.toHaveProperty("databases");
    expect(dashboardRoutes.plans.path).toBe("/plans");
    expect(dashboardRoutes.providerAccounts.path).toBe("/plans/accounts");
    expect(buildDashboardPath(dashboardRoutes.workspaceDetail, { workspaceId: "workspace 1" }))
      .toBe("/workspaces/workspace%201");
  });
});
