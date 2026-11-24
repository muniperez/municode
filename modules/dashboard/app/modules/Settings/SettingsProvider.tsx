import { createContext, useContext, useEffect, useRef, useState, type PropsWithChildren, type RefObject } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import { AccountFlowForm } from "@/modules/Auth/AccountFlowForm";
import { SessionSettingsCard } from "@/modules/Auth/SessionSettingsCard";
import { ApiKeysModule } from "@/modules/ApiKeys/ApiKeysModule";
import { BudgetsSettingsModule } from "@/modules/Budgets/BudgetsSettingsModule";
import { ProvidersModule } from "@/modules/Providers/ProvidersModule";
import { GitHubSettingsModule } from "./GitHubSettingsModule";
import { TeamSettingsModule } from "./TeamSettingsModule";

const groups = [
  { label: "Account", items: [{ id: "account", label: "Account" }] },
  { label: "Workspace administration", items: [
    { id: "plans", label: "Plans" }, { id: "budgets", label: "Budgets" }, { id: "team", label: "Team" },
    { id: "api-keys", label: "API Keys" },
  ] },
  { label: "Integrations", items: [{ id: "github", label: "GitHub" }] },
] as const;
type SettingsItem = typeof groups[number]["items"][number]["id"];
const items = groups.flatMap(group => [...group.items]);
const SettingsContext = createContext<{ openSettings: (item?: SettingsItem, opener?: HTMLElement) => void }>({
  openSettings: () => {},
});
export const useSettings = () => useContext(SettingsContext);

export function SettingsProvider({ children, fallbackFocus }: PropsWithChildren<{
  fallbackFocus: RefObject<HTMLElement | null>;
}>) {
  const location = useLocation();
  const navigate = useNavigate();
  const [selected, setSelected] = useState<SettingsItem>("account");
  const [open, setOpen] = useState(false);
  const [plansView, setPlansView] = useState<"plans" | "accounts">("plans");
  const opener = useRef<HTMLElement | null>(null);
  const navigation = useRef<HTMLElement>(null);
  const openSettings = (item: SettingsItem = "account", control?: HTMLElement) => {
    opener.current = control ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    setSelected(item);
    setPlansView("plans");
    setOpen(true);
  };
  useEffect(() => {
    const requested = new URLSearchParams(location.search).get("settings");
    const item = items.find(candidate => candidate.id === requested);
    if (item) {
      setSelected(item.id);
      setPlansView("plans");
      opener.current = null;
      setOpen(true);
    }
  }, [location.search]);
  function close() {
    setOpen(false);
    const search = new URLSearchParams(location.search);
    if (search.has("settings")) {
      search.delete("settings");
      search.delete("organizationId");
      navigate({ pathname: location.pathname, search: search.toString(), hash: location.hash }, { replace: true });
    }
  }
  const accountReturn = new URLSearchParams(location.search);
  accountReturn.set("settings", "account");

  return <SettingsContext.Provider value={{ openSettings }}>
    {children}
    <Dialog open={open} onOpenChange={value => { if (!value) close(); }}>
      <DialogContent className="flex h-[min(48rem,90dvh)] max-w-5xl flex-col gap-0 overflow-hidden p-0"
        onOpenAutoFocus={event => {
          event.preventDefault();
          navigation.current?.querySelector<HTMLButtonElement>(`[data-settings-item="${selected}"]`)?.focus();
        }}
        onCloseAutoFocus={event => {
          event.preventDefault();
          (opener.current?.isConnected ? opener.current : fallbackFocus.current)?.focus();
        }}>
        <div className="border-b px-6 py-5 pr-12">
          <DialogTitle>Settings</DialogTitle>
          <DialogDescription className="sr-only">Manage your account, workspace administration and integrations.</DialogDescription>
        </div>
        <div className="flex min-h-0 flex-1 flex-col sm:flex-row">
          <nav ref={navigation} aria-label="Settings" className="shrink-0 overflow-y-auto border-b bg-muted/30 p-3 max-sm:max-h-[35%] sm:w-56 sm:border-b-0 sm:border-r"
            onKeyDown={event => {
              if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
              const buttons = Array.from(navigation.current?.querySelectorAll<HTMLButtonElement>("button") ?? []);
              const index = document.activeElement instanceof HTMLButtonElement ? buttons.indexOf(document.activeElement) : -1;
              if (index < 0) return;
              event.preventDefault();
              const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
                : (index + (event.key === "ArrowDown" ? 1 : -1) + buttons.length) % buttons.length;
              buttons[next]?.focus();
              buttons[next]?.click();
            }}>
            {groups.map(group => <div key={group.label} className="mb-3 last:mb-0">
              <h2 className="px-2 py-2 text-xs font-medium text-muted-foreground">{group.label}</h2>
              <div className="space-y-1">{group.items.map(item => <button key={item.id} type="button"
                data-settings-item={item.id} aria-pressed={selected === item.id} aria-controls="app-settings-panel"
                onClick={() => setSelected(item.id)}
                className={`flex w-full rounded-md px-3 py-2 text-left text-sm outline-none transition-colors hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring ${selected === item.id ? "bg-accent font-medium text-accent-foreground" : ""}`}>
                {item.label}
              </button>)}</div>
            </div>)}
          </nav>
          <section id="app-settings-panel" aria-label={items.find(item => item.id === selected)?.label}
            className="min-h-0 min-w-0 flex-1 overflow-y-auto p-4 sm:p-6">
            {selected === "account" && <div className="space-y-6">
              <AccountFlowForm kind="settings" returnTo={`${location.pathname}?${accountReturn}${location.hash}`} />
              <SessionSettingsCard />
            </div>}
            {selected === "plans" && <ProvidersModule view={plansView} onViewChange={setPlansView} />}
            {selected === "budgets" && <BudgetsSettingsModule />}
            {selected === "team" && <TeamSettingsModule />}
            {selected === "api-keys" && <ApiKeysModule />}
            {selected === "github" && <GitHubSettingsModule />}
          </section>
        </div>
      </DialogContent>
    </Dialog>
  </SettingsContext.Provider>;
}
