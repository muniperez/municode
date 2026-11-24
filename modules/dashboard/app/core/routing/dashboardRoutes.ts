import { HardDrive } from "lucide-react";
import type { ComponentType } from "react";

export type DashboardRouteLayout = "guest" | "main" | "standalone";

export type DashboardRouteDefinition = {
  key: string;
  name: string;
  path: string;
  layout: DashboardRouteLayout;
  nav?: {
    label: string;
    icon: ComponentType<{ className?: string }>;
    group: "main" | "settings";
  };
};

function defineRoute(route: DashboardRouteDefinition) {
  return route;
}

export const dashboardRoutes = {
  github: defineRoute({ key: "github", name: "GitHub", path: "/settings/github", layout: "main" }),
  setup: defineRoute({ key: "setup", name: "First operator setup", path: "/setup", layout: "standalone" }),
  gettingStarted: defineRoute({ key: "gettingStarted", name: "Provider and plan guide", path: "/getting-started", layout: "main" }),
  signIn: defineRoute({ key: "signIn", name: "Sign in", path: "/sign-in", layout: "guest" }),
  recovery: defineRoute({ key: "recovery", name: "Recover account", path: "/recovery", layout: "guest" }),
  onboarding: defineRoute({ key: "onboarding", name: "Onboarding", path: "/onboarding", layout: "standalone" }),
  workspaces: defineRoute({ key: "workspaces", name: "Workspaces", path: "/workspaces", layout: "main", nav: { label: "Workspaces", icon: HardDrive, group: "main" } }),
  workspaceDetail: defineRoute({ key: "workspaceDetail", name: "Workspace", path: "/workspaces/:workspaceId", layout: "main" }),
  plans: defineRoute({ key: "plans", name: "Plans", path: "/plans", layout: "main" }),
  providerAccounts: defineRoute({ key: "providerAccounts", name: "Provider accounts", path: "/plans/accounts", layout: "main" }),
  providers: defineRoute({ key: "providers", name: "Providers", path: "/providers", layout: "main" }),
  apiKeys: defineRoute({ key: "apiKeys", name: "API Keys", path: "/settings/api-keys", layout: "main" }),
  teamSettings: defineRoute({ key: "teamSettings", name: "Team", path: "/settings/team", layout: "main" }),
  settings: defineRoute({ key: "settings", name: "Settings", path: "/settings/account", layout: "main" }),
} as const;

export const navigationRoutes = Object.values(dashboardRoutes).filter((route) => route.nav?.group === "main");
export const settingsRoutes = Object.values(dashboardRoutes).filter((route) => route.nav?.group === "settings");

export function buildDashboardPath(route: DashboardRouteDefinition, params: Record<string, string> = {}) {
  return Object.entries(params).reduce((path, [key, value]) => path.replace(`:${key}`, encodeURIComponent(value)), route.path);
}
