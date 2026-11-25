# Use Municode Workspaces

Sign in at the console origin supplied by your operator. The first owner completes
one-time installation setup; subsequent users receive access from an organization
owner or admin. Keep the console and API on their HTTPS origins and do not copy
session cookies or API keys into files, command history or support reports.

## Passkeys

When your operator enables passkeys, open **Settings → Account** and choose
**Add passkey** to save one with this device's screen lock, a phone or a
security key. Then choose **Sign in with passkey** on the sign-in page. Your
password keeps working, and a canceled passkey prompt leaves the password form
available. The same account settings list your passkeys and remove them; the
last remaining sign-in method cannot be removed. Adding or removing a passkey,
like changing your password, may first ask you to confirm it's you and then
signs out your other browser sessions. Passkey sign-in also approves `workspaces
login` requests from the CLI.

## Dashboard

The packaged dashboard is a static client of the standalone API. It lists
organization workspaces, offers provider/image/plan selections that the operator
has admitted, and follows the launch URL returned by the API. T3 and each preview
use a distinct generation-bound origin; an expired launch should be repeated
from the dashboard rather than by reusing a URL.

The dashboard child project owns its UI and browser behavior. Delivery only
serves its built `dist` output and applies no application-hosting redirects or
account assumptions.

## CLI

The release archive stores executables below `cli/<runtime>/municode`
(`municode.exe` on Windows) plus the canonical `workspaces` wrapper
(`workspaces.cmd` on Windows). Install the matching directory contents into a
protected location on your `PATH`. The CLI child project owns command behavior
and exact help; run `workspaces --help` or `municode --help` from its released
binary for the accepted commands.

Operators use `workspaces seed` before ordinary authenticated workspace commands.
`municode seed` and `municode workspaces seed` are equivalent compatibility
spellings. Seed never authenticates a workspace user or registers its bootstrap
provider credential automatically. After first-owner setup, register a provider,
image and plan in the dashboard before creating the first coding VM.

Use the API URL and a short-lived or scoped key issued by your installation.
Use `workspaces:read` for inspection and `workspaces:write` only for mutations.
Do not enter provider credentials on the command line where process inspection
can expose them.

## Workspace lifecycle

After selecting an admitted provider, image and plan, create a retained primary
workspace, open T3 or an authorized preview, and stop it when finished. Ordinary
stop retains the primary volume and releases compute. Resume restores a new
generation of that volume. Deletion is a permanent, separately confirmed
operation and follows the operator's retention policy.

If stop reports a live T3 terminal shell, finish its jobs and run `exit` at the shell
prompt. Closing the terminal UI or browser tab does not prove that the shell exited.
If the shell is no longer visible in T3, contact your operator. Stop keeps waiting
and retries automatically once the shell exits.

Ephemeral Git-backed workspaces are safe only for the selected repository,
base ref and recovered commit. Ignored or local-only files must not be assumed
to survive. The dashboard or CLI must surface a failed or unknown operation
instead of treating it as complete.

## Budgets

**Settings → Budgets** compares an estimated cost with optional organization
budgets. Every organization member can view budgets; only owners
and admins create, edit or delete them (API key scopes `budgets:read` and
`budgets:write`). Budgets only inform. Crossing a threshold never blocks
creating or starting a workspace and never stops resources.

Each budget has an amount per period: a calendar month, an ISO week (Monday to
Sunday) or a calendar quarter, all in UTC. Its scope narrows what is counted.
Provider accounts are applied first. Within them, you can select whole
workspaces (all their VMs and storage) or individual VMs (only their compute).
Empty selections include everything, and selections that no longer exist are
reported as warnings. The API and webhook payload name the limit `amount` with a
`period` of `month`, `week` or `quarter`, and the selections `providerAccountIds`,
`workspaceIds` and `vmIds`.

The estimate is not an invoice and does not include provider-reported charges.
It assumes every VM that currently reserves compute runs for the whole period
and every retained volume is kept for the whole period. A full calendar month
counts as 720 hours, so a quarter is three monthly amounts. A week counts its
actual hours, split at a month boundary, and monthly caps and minimum charges
apply separately in each calendar month. The card also shows the monthly run
rate. Prices come from the
operator's price catalog for each provider, region and size, including hourly
billing, monthly caps, per-second billing and minimum charges. Without a
catalog entry, the rate recorded when the VM was selected or the workspace was
created is used, and the provider's assumptions say a fallback rate was used.
The total is grouped by provider account, and each provider lists the prices
and billing rules it assumed. Workspaces priced in another currency are
excluded and listed separately, never converted or summed. A missing price
marks the estimate incomplete and is not counted as zero. Prices older than the
operator's limit are marked stale. Viewing budgets never records anything.

The worker checks budgets in the background (every five minutes by default) and
notifies once per threshold per budget period (the UTC month, ISO week or
quarter). If several thresholds are
crossed at once, one notification names the highest and lists all of them in
`crossedThresholds`. Changing the amount, period, thresholds or scope starts a new
alert baseline; changing only the name or notification settings does not. Each budget
chooses its channels:

- **In-app** (on by default): a `budget.threshold_crossed` audit event and a
  dismissible dashboard notice.
- **Signed webhook**: a JSON `POST` to a public HTTPS URL. The full URL is
  write-only; the dashboard shows only its origin. A signing secret is shown
  once when the webhook is created or the secret is rotated. Each request has
  `X-Municode-Event: budget.threshold_crossed`, `X-Municode-Delivery` (a stable
  ID for de-duplication), `X-Municode-Timestamp` (Unix seconds) and
  `X-Municode-Signature: sha256=<hex>`, where the hex is
  `HMAC-SHA256(secret, "<timestamp>.<raw body>")`. Compare in constant time and
  reject stale timestamps. Any 2xx response counts as delivered; other results
  are retried with exponential backoff and then marked failed. Redirects are not
  followed, and targets that resolve to private or loopback addresses are
  refused.

- **Email**: one plain-text email per recipient. Recipients are the
  organization's owners and admins (the default) or all members. Email goes to
  each member's account address; you cannot add other addresses. Membership and
  role are checked again just before sending, so removed members get nothing.
  The email names the budget period and its dates and lists the threshold, the
  estimated projection for the period, the top contributors,
  warnings and assumptions. It states that the figures are estimates and that
  budgets never block or stop resources. It never contains webhook URLs, signing
  secrets or credentials. Email needs an operator to configure outbound email.
  Until then the budget card shows "not configured" and no email is queued or
  sent.

**Recent notifications** on each budget card shows each delivery's status,
attempts and last error. Turning a channel off cancels its pending deliveries.

**Provider-reported charges** appear on each budget card separately from the
estimate. For each provider account in the budget's scope, the card shows the
month-to-date amount the provider reports for the current UTC month (also for
weekly and quarterly budgets), in the provider's currency, with the time it was reported ("as of"). Totals are listed
per currency; different currencies are never converted or summed, and the
amounts are never added to the estimate or used for threshold alerts. Each
amount covers the whole provider account, including resources Municode does not
manage. The worker refreshes them about once an hour. An amount older than
three hours is marked **Stale**. If a refresh fails, the card shows **Refresh
failed** with the last reported amount, if any. Missing data shows as **Not
available**, never as zero.

- **DigitalOcean**: `month_to_date_usage` from the read-only customer balance
  endpoint. A custom-scoped API token needs the `billing:read` scope.
- **Vultr**: the account's pending charges (accrued since the last invoice). The
  API key's user needs billing access.
- **Hetzner Cloud** has no billing API, so its accounts always show "Provider-reported
  charges not available".

Only the aggregated amount is stored; invoice line items are never retrieved or
kept.

## Removing a checkout

For a checkout with scratch-mounted build outputs, use the dashboard's confirmed
**Delete folder** operation on the checkout under `/workspace/repos`. Finish work
and close processes using it first, fetch disk usage, verify and type the exact
folder path, and acknowledge permanent deletion. Wait for **Folder deleted**
before cloning to the same path again. A failed, cancelled, partial or unknown
result requires another scan to inspect what remains, not an assumption of success.

Ordinary `rm -rf` can exit nonzero with `EBUSY` while scratch outputs are mounted;
later watcher cleanup cannot change that exit status. Confirmed checkout deletion
requires a qualified image containing this support and refuses unsafe mount,
identity or active-use conditions rather than deleting across them.
