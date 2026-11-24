import type { RouteObject } from "react-router-dom";
import { Navigate } from "react-router-dom";
import { AppShell } from "./core/layout/AppShell";
import { AuthLayout } from "./core/layout/AuthLayout";
import { ApiKeysModule } from "./modules/ApiKeys/ApiKeysModule";
import { AccountFlowForm } from "./modules/Auth/AccountFlowForm";
import { ProviderContinuationModule } from "./modules/Auth/ProviderContinuationModule";
import { SessionSettingsCard } from "./modules/Auth/SessionSettingsCard";
import { SignInModule } from "./modules/Auth/SignInModule";
import { CliLoginModule } from "./modules/CliLogin/CliLoginModule";
import { OnboardingModule } from "./modules/Onboarding/OnboardingModule";
import { ProviderAccountsModule, ProvidersModule } from "./modules/Providers/ProvidersModule";
import { GitHubCallbackModule, GitHubSettingsModule } from "./modules/Settings/GitHubSettingsModule";
import { TeamSettingsModule } from "./modules/Settings/TeamSettingsModule";
import { ProviderPlanGuide } from "./modules/Setup/ProviderPlanGuide";
import { SetupModule } from "./modules/Setup/SetupModule";
import { WorkspacesModule } from "./modules/Workspaces/WorkspacesModule";

export const routes: RouteObject[] = [
  { path: "/setup", element: <SetupModule /> },
  {
    element: <AuthLayout />,
    children: [
      { path: "/sign-in", element: <SignInModule /> },
      { path: "/recovery", element: <AccountFlowForm kind="recovery" /> },
    ],
  },
  { path: "/onboarding", element: <OnboardingModule /> },
  { path: "/cli/login", element: <CliLoginModule /> },
  { path: "/sign-in/continue", element: <ProviderContinuationModule /> },
  {
    element: <AppShell />,
    children: [
      { path: "/", element: <Navigate to="/workspaces" replace /> },
      { path: "/getting-started", element: <ProviderPlanGuide /> },
      { path: "/workspaces", element: <WorkspacesModule /> },
      { path: "/workspaces/:workspaceId", element: <WorkspacesModule /> },
      { path: "/plans", element: <ProvidersModule /> },
      { path: "/plans/accounts", element: <ProviderAccountsModule /> },
      { path: "/providers", element: <Navigate to="/plans" replace /> },
      { path: "/settings/api-keys", element: <ApiKeysModule /> },
      {
        path: "/settings/account",
        element: (
          <div className="space-y-6">
            <AccountFlowForm kind="settings" returnTo="/settings/account" />
            <SessionSettingsCard />
          </div>
        ),
      },
      { path: "/settings/team", element: <TeamSettingsModule /> },
      { path: "/settings/github", element: <GitHubSettingsModule /> },
      { path: "/settings/github/callback", element: <GitHubCallbackModule /> },
      { path: "/settings/github/installed", element: <GitHubCallbackModule /> },
    ],
  },
  { path: "*", element: <Navigate to="/workspaces" replace /> },
];
