# Use Municode Workspaces

Sign in at the console origin supplied by your operator. The first owner completes
one-time installation setup; subsequent users receive access from an organization
owner or admin. Keep the console and API on their HTTPS origins and do not copy
session cookies or API keys into files, command history or support reports.

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

Ephemeral Git-backed workspaces are safe only for the selected repository,
base ref and recovered commit. Ignored or local-only files must not be assumed
to survive. The dashboard or CLI must surface a failed or unknown operation
instead of treating it as complete.

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
