# Municode runtime

The API manages the database, lifecycle reservations, enrollment and gateway authorization.
`Municode.Worker` reconciles provider resources and operates the generation-bound guest over SSH.
`Municode.Gateway` relays browser HTTP and WebSocket traffic to the worker's private SSH forwards.
The worker and API use the same Municode database and OpenBao KV mount. The gateway needs only its
service credential and private network access to the API and worker.

## Configure a fresh installation

1. Follow the [control-plane setup](control-plane.md), apply migrations and register the provider
   account and plan. `municode seed` does this by default: it registers `Default provider`, a
   default plan and a stock Ubuntu image, so no further step here is needed for a first coding VM.
   A stock image (`Source: ubuntu`, the provider's Ubuntu 24.04 image ID) is provisioned over SSH by
   the worker; see [stock Ubuntu provisioning](../../templates/development-workspaces/README.md#stock-ubuntu-provisioning-default).
   For an optional [prepared image](../../templates/development-workspaces/README.md#prepared-image),
   use the **installed** manifest's SHA-256, matching provider snapshot ID, account, region, T3
   version and protocol in the image registration. Set current pricing before enabling workspace
   lifecycle requests.
2. Create a dedicated Municode worker SSH keypair, for example with
   `ssh-keygen -t ed25519 -f <protected-directory>/worker-ssh`. Set
   `WorkspaceSsh:PrivateKeyFile` to its absolute owner-only path and
   `WorkspaceSsh:PublicKey` to the OpenSSH public key. The worker uses port 22, one independently
   generated pinned server key per VM generation and provider firewall rules restricted to
   `DevelopmentWorkspaceWorker:ManagementAddresses` (the worker's exact egress host CIDRs).
   Seed fills this with the control-plane VPS's public IPv4 `/32`. `install.sh --management-address`
   does the same for a new policy. Until it is set, workspace creation is rejected with
   `workspace_management_addresses_missing` and the worker provisions nothing.
   Workspace provisioning has no global on/off switch. A legacy `DevelopmentWorkspaces:Enabled`
   value is ignored. Availability follows current pricing, profiles, approved images and plans.
   An empty catalog reports the first missing prerequisite.
   Set `ApiBaseUrl` to the HTTPS API base reachable from the guest for one-time enrollment.
3. Set `DevelopmentWorkspaceGateway:ServiceCredentialFile` on API and gateway to protected files
   containing the same independently generated 32–512 character secret. Set the gateway's
   `ControlPlaneUrl` to the API's origin and configure the same `GatewayDomain` and private
   relay range throughout. Set `DevelopmentWorkspaceWorker:RelayLockFile` to an absolute path
   in an existing directory writable by the worker. Use a single worker on the relay host per
   installation. When using containers, mount the lock directory persistently and share its path
   between replacement processes. Keep the private relay range reserved exclusively for the worker.
4. Terminate TLS for the wildcard gateway domain at a trusted reverse proxy. Preserve the request
   Host, set `X-Forwarded-Proto: https`, and pass WebSocket upgrades. Set
   `TrustedProxyAddress` to the proxy's exact private address and restrict access to the gateway
   listener and the worker relay range by network policy. The API's internal gateway routes need
   services-only ingress. Configure the same `HttpTimeoutSeconds` on worker and gateway so a
   recycled relay port stays fenced for the duration of any outstanding connection attempt.
   Keep full request URLs out of request logs, especially at the TLS proxy.
5. Supply the [example configuration](municode.example.json) via an absolute
   `DevelopmentWorkspaces__ConfigurationFile` and environment overrides. Start the processes on
   distinct bindings as needed (`ASPNETCORE_URLS` for API and gateway). The API and worker may
   use separate scoped OpenBao tokens. The worker needs provider account secret reads and workspace
   runtime secret writes, reads and metadata deletion. Ephemeral Git also uses the
   organization's Municode GitHub App or explicitly approved repository-scoped credentials.

The gateway's internal certificate check at `/internal/certificate?domain=…` returns success
only to the configured proxy for an authorized generation/service hostname. It can be used
as a certificate admission endpoint. The API also supports a wildcard certificate provisioned
outside this flow.

## Release a workspace image

Run this gate from the operator workstation, not GitHub Actions. Build and generalize a
[disposable builder](../../templates/development-workspaces/README.md#prepared-image), power it
off and capture a **private** snapshot. Register an immutable candidate image key and a test
plan on an isolated acceptance control plane or in a dedicated test organization with a
provider account registration not shared with user plans. The normal create API still requires
`Enabled`, `RuntimeQualified`, `QualifiedAt` and `T3ConnectForNewWorkspaces` in that **test**
registration. These admission fields are not release approval. Do not enable the candidate
in the user catalog yet. Check that the test API and worker both map the key to the same exact
`ProviderImageId` and installed `ManifestSha256`; never repoint an existing image key.

From the repository root with Node.js 22 LTS, give the runner a short-lived API key scoped
to the test organization with only `workspaces:read` and `workspaces:write`. Put the key in
an owner-only regular file (`chmod 600`), outside the repository. Supply the path, not its
contents. No provider credentials are needed by the runner.

```sh
node scripts/workspace-image-release-acceptance.mjs \
  --api https://api.example.com --gateway-domain workspace.example.com \
  --key-file /protected/path/acceptance.key --organization TEST_ORG_ID \
  --image CANDIDATE_IMAGE_KEY --provider-image SNAPSHOT_ID \
  --profile TEST_PROFILE_ID --plan TEST_PLAN_ID
```

The runner creates one disposable workspace, writes a known file as the coding user over the
normal T3 gateway, closes the terminal and session, stops the VM, starts a new generation
on the same volume, compares the file byte for byte through a fresh T3 session, then stops
and permanently deletes the workspace. Its sanitized JSON report is in
`.local/artifacts/workspace-image-release/`. The provider snapshot ID is operator-supplied;
the API exposes the workspace's registered image key, not the underlying snapshot ID.
The runner checks that key on both generations. On failure it records the failed step and
tries normal Stop and Delete again without changing the failed result. Cleanup closes any T3
terminal it opened, retries server errors and lost connections until its deadline, and never
sends Stop or Delete until it reads back the workspace's unique name. If creation's response
is lost, it searches for its unique name instead of sending a second Create. If cleanup is
unconfirmed (including after a force-kill), locate the workspace using the report ID or unique
name in the test organization and reconcile through normal Stop and permanent Delete.

A passing report requires the retained deletion record (`phase` and `volumeStatus` both
`deleted`, with cleared resource IDs and reservations). The worker confirms server and volume
absence and requests deletion of related resources. The VM's `/resource-history` endpoint
only holds utilization samples, not provider inventory. **Before promotion**, use provider
inventory and activity history in the same account/project and region to confirm:

- Neither generation's server ID nor the retained volume ID in the report is present.
  Search for additional resources with the same workspace ownership, not just those IDs.
- Both generations' associated firewalls and any workspace-owned IPs and tags are absent.
  On Hetzner, check servers, volumes, firewalls and primary IPs by the `workspace=<id>` label.
  On DigitalOcean, check Droplets and volumes using `muw:workspace:<id>` and trace each
  Droplet's `muw-fw-…` tag to its dedicated firewall and tag. On Vultr, check instances with
  `muw:workspace=<id>`, block storage with `w=<id>` in its `muw;…` label, and the firewall
  groups attached to those instances. Use provider activity history or record attachments
  during the run if they cannot be identified after deletion.

Record the inventory result with the lifecycle report. Promote only that exact snapshot
after both pass. On any failure, timeout or unresolved resource, keep the candidate private,
record the image ID and failed step, and keep the previous approved image available.

## Runtime interfaces

- An authorized launch via `POST /api/development-workspaces/{workspaceId}/vms/{vmId}/launch`
  accepts `{ "generation": 1, "service": "t3" }`. Open the returned URL as a navigation.
  It redeems a short-lived, single-use launch code for a secure host-only cookie and redirects
  to `/`. Use the returned expiry to determine whether a fresh launch is needed. The API
  checks organization authority, user/API key validity, generation, route expiry and runtime health.
  The gateway reauthorizes active sockets and ends streams when their route or bearer changes.
  It returns 401 only when the API explicitly rejects the session; an unavailable or failing
  API yields a retryable 503 and leaves the cookie intact.
- Register named preview services in `DevelopmentWorkspaceWorker:PreviewServices` and request
  the corresponding service at launch. Each healthy service gets a separate origin from T3
  and from every other service. The optional `connect` service is restricted to a primary's
  admitted T3 Connect receiver on guest loopback port 774. The receiver manages optional desktop
  discovery and remote enrollment, using user-owned relay configuration. The root-managed
  gateway bearer stays server-side.
- The worker pins the retained primary volume's identity and filesystem UUID and retires just
  compute on ordinary stop. Resume allocates a new generation and remounts the same volume.
  Permanent deletion requires all generations to stop and verifies provider identity and
  absence before releasing the reservation. Ephemeral VMs have no retained volume. Their
  authorized recovery path verifies the exact saved Git ref and commit before cloning, and
  creates a new per-generation branch. Local-only or ignored data requires a deliberate discard.
- Disk usage and directory deletion use generation-bound filesystem jobs with concrete target
  identity checks. Resource samples, idle activity and T3 work inventory are captured through the
  generation-bound guest supervisor. Unknown runtime or provider state keeps reservations intact.
  The gateway exposes low-cardinality .NET metrics through the `Municode.Gateway` meter.

## Build and verify locally

.NET 10, Python 3.11+, Git and Node.js are sufficient for the default synthetic suite.
The browser fixture also needs OpenSSL and Chromium's Linux shared libraries.

```sh
dotnet build src/Municode.Worker
dotnet build src/Municode.Gateway
dotnet test Municode.slnx --results-directory .local/artifacts/test-results
templates/development-workspaces/test.sh
python3 -B templates/development-workspaces/image/test-t3-candidate.py
node --test scripts/workspace-image-release-acceptance.test.mjs

dotnet publish src/Municode.Worker -c Release -o .local/artifacts/publish/worker
dotnet publish src/Municode.Gateway -c Release -o .local/artifacts/publish/gateway
```

The PostgreSQL matrix is optional. Set `MUNICODE_TEST_POSTGRES_PORT` to the TCP port
of a **disposable loopback** PostgreSQL instance with trust authentication for the test
superuser `postgres`. The suite creates, migrates and drops isolated test databases.
SQLite tests exercise the Municode initial migration and run by default.

Run the gateway benchmark in Release mode. It starts synthetic loopback control and guest
endpoints and prints JSON to stdout.

```sh
mkdir -p .local/artifacts/benchmarks
for scenario in footprint relay admission; do
  dotnet run -c Release --project tools/workspace-gateway-benchmark -- "$scenario" \
    > ".local/artifacts/benchmarks/$scenario.json"
done
```

The browser fixture starts the actual standalone gateway with synthetic control and guest
endpoints. It verifies certificate admission, one-time launch, secure host-only cookies,
preview isolation, T3 ticket and WebSocket relay, and revocation. Use the pinned browser
test dependencies below and keep Chromium, its profile, screenshots and report in the
ignored artifacts directory.

```sh
mkdir -p .local/artifacts/browser .local/artifacts/tmp
npm_config_cache="$PWD/.local/artifacts/npm-cache" npm install --prefix .local/artifacts/browser \
  --no-save playwright@1.61.0 ws@8.21.0
PLAYWRIGHT_BROWSERS_PATH="$PWD/.local/artifacts/browser/chromium" TMPDIR="$PWD/.local/artifacts/tmp" \
  .local/artifacts/browser/node_modules/.bin/playwright install chromium --only-shell
dotnet build src/Municode.Gateway
node scripts/workspace-browser-acceptance.mjs
```

The synthetic suite covers provisioning, enrollment, relay, persistence, deletion, Git recovery
and guest protocols. The prepared-image documentation describes separate component checks for
real mounts, systemd and T3; neither synthetic nor native checks replace the release lifecycle.

The dashboard browser acceptance drives the built dashboard against a stateful, credential-free
mock of the workspace API at desktop, 390 px and 320 px widths. It covers the list, filters,
details and direct links; create with its single automatic start and idempotent retry; startup,
T3 and preview launch, stop and revisit; temporary VMs, Git-save recovery, discard and stop all;
stale generations, rejected and expired launches; the read-only role; and empty, unavailable and
refresh-error states. It fails on any unexpected request, mutation or external origin. CI runs
it with the runner's Google Chrome (`PLAYWRIGHT_CHANNEL=chrome`); locally, reuse the Chromium
shell installed above. Screenshots go to `.local/artifacts/dashboard-browser`.

```sh
npm ci --prefix modules/dashboard
npm run build --prefix modules/dashboard
cd modules/dashboard
npm exec --package=playwright@1.61.0 -- node ../../scripts/dashboard-browser-acceptance.mjs
```
