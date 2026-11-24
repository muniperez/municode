import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Budget, BudgetProviderChargeStatus } from "@/api/budgetsApi";
import { formatPrice } from "@/modules/Workspaces/WorkspaceCostEstimate";

const statusLabel: Record<BudgetProviderChargeStatus, string> = {
  available: "Current", stale: "Stale", failed: "Refresh failed", unsupported: "Not available", not_retrieved: "Not available yet",
};

function amount(value: number | null, currency: string | null) {
  if (value === null || currency === null) return "Not available";
  return formatPrice(value, currency) ?? `${value} ${currency}`;
}

/** What providers report they have charged, shown apart from the estimate and never summed across currencies. */
export function ProviderChargesSection({ budget }: { budget: Budget }) {
  const charges = budget.providerCharges;
  if (!charges) return null;
  return <section aria-label={`${budget.name} provider-reported charges`} className="space-y-2">
    <h4 className="font-medium">Provider-reported charges</h4>
    {charges.totals.length > 0
      ? <p>Reported for {charges.period} so far: {charges.totals.map(total =>
        `${amount(total.amount, total.currency)} (${total.accountCount} ${total.accountCount === 1 ? "account" : "accounts"})`).join(" · ")}</p>
      : <p className="text-muted-foreground">Provider-reported charges not available for {charges.period}.</p>}
    {charges.accounts.length > 0 && <Table aria-label={`${budget.name} provider-reported charges by account`}>
      <TableHeader><TableRow><TableHead>Provider account</TableHead><TableHead>Reported month to date</TableHead>
        <TableHead>As of</TableHead><TableHead>Status</TableHead></TableRow></TableHeader>
      <TableBody>{charges.accounts.map(item => <TableRow key={item.providerAccountId}>
        <TableCell>{item.providerName} <span className="text-muted-foreground">({item.providerType})</span></TableCell>
        <TableCell>{amount(item.amount, item.currency)}</TableCell>
        <TableCell>{item.asOf ? new Date(item.asOf).toLocaleString() : "—"}</TableCell>
        <TableCell><span className="font-medium">{statusLabel[item.status]}</span>
          <span className="block text-xs text-muted-foreground">{item.message}</span></TableCell>
      </TableRow>)}</TableBody>
    </Table>}
    {charges.notes.length > 0 && <ul className="list-disc space-y-1 pl-5 text-xs text-muted-foreground">
      {charges.notes.map(note => <li key={note}>{note}</li>)}
    </ul>}
  </section>;
}
