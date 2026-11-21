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
(`municode.exe` on Windows). Install the matching executable into a protected
location on your `PATH`. The CLI child project owns command behavior and exact
help; run `municode --help` from its released binary for the accepted commands.

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
