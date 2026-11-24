import { useRef, useState } from "react";
import { Outlet } from "react-router-dom";
import { Menu } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Sheet,
  SheetContent,
  SheetTitle,
  SheetDescription,
  SheetTrigger,
} from "@/components/ui/sheet";
import { AuthenticatedArea } from "../auth/AuthenticatedArea";
import { SidebarNavigation } from "./SidebarNavigation";
import { SettingsProvider } from "@/modules/Settings/SettingsProvider";
import { BudgetAlertBanner } from "@/modules/Budgets/BudgetAlertBanner";
export function AppShell() {
  const [open, setOpen] = useState(false);
  const main = useRef<HTMLElement>(null);
  return (
    <AuthenticatedArea>
      <SettingsProvider fallbackFocus={main}>
        <div className="min-h-screen bg-background">
          <aside className="fixed inset-y-0 left-0 hidden w-64 border-r border-sidebar-border bg-sidebar text-sidebar-foreground md:block">
            <SidebarNavigation onNavigate={() => {}} />
          </aside>
          <div className="min-w-0 md:pl-64">
            <header className="flex h-14 items-center border-b border-border px-4 md:hidden">
              <Sheet open={open} onOpenChange={setOpen}>
                <SheetTrigger asChild>
                  <Button
                    variant="ghost"
                    size="icon"
                    aria-label="Open navigation"
                  >
                    <Menu className="h-5 w-5" />
                  </Button>
                </SheetTrigger>
                <SheetContent side="left" className="w-72 bg-sidebar p-0 text-sidebar-foreground">
                  <SheetTitle className="sr-only">Navigation</SheetTitle>
                  <SheetDescription className="sr-only">
                    Workspace navigation and account
                  </SheetDescription>
                  <SidebarNavigation onNavigate={() => setOpen(false)} />
                </SheetContent>
              </Sheet>
              <span>Municode Workspaces</span>
            </header>
            <main ref={main} tabIndex={-1} className="mx-auto min-w-0 max-w-7xl p-4 md:p-6">
              <BudgetAlertBanner />
              <Outlet />
            </main>
          </div>
        </div>
      </SettingsProvider>
    </AuthenticatedArea>
  );
}
