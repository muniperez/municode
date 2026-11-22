# Workspaces runtime

The API manages the database, lifecycle reservations, enrollment and gateway authorization.
`Workspaces.Worker` reconciles provider resources and operates the generation-bound guest over SSH.
`Workspaces.Gateway` relays browser HTTP and WebSocket traffic to the worker's private SSH forwards.
The worker and API use the same Workspaces database and OpenBao KV mount. The gateway needs only its
service credential and private network access to the API and worker.

## Configure a fresh installation

1. Follow the [control-plane setup](control-plane.md), apply migrations and register the provider
   account and plan. See [prepared images](../../templates/development-workspaces/README.md) for the
   Ubuntu amd64 image builder, pinned inputs, and qualification. Use the **installed** manifest's
   SHA-256, matching provider snapshot ID, account, region, T3 version and protocol in the image
   registration. Set current pricing before enabling workspace lifecycle requests.
2. Create a dedicated Workspaces worker SSH keypair, for example with
   `ssh-keygen -t ed25519 -f <protected-directory>/worker-ssh`. Set
   `WorkspaceSsh:PrivateKeyFile` to its absolute owner-only path and
   `WorkspaceSsh:PublicKey` to the OpenSSH public key. The worker uses port 22, one independently
   generated pinned server key per VM generation and provider firewall rules restricted to
   `DevelopmentWorkspaceWorker:ManagementAddresses` (the worker's exact egress host CIDRs).
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
5. Supply the [example configuration](workspaces.example.json) via an absolute
   `DevelopmentWorkspaces__ConfigurationFile` and environment overrides. Start the processes on
   distinct bindings as needed (`ASPNETCORE_URLS` for API and gateway). The API and worker may
   use separate scoped OpenBao tokens. The worker needs provider account secret reads and workspace
   runtime secret writes, reads and metadata deletion. Ephemeral Git also uses the
   organization's Workspaces GitHub App or explicitly approved repository-scoped credentials.

The gateway's internal certificate check at `/internal/certificate?domain=…` returns success
only to the configured proxy for an authorized generation/service hostname. It can be used
as a certificate admission endpoint. The API also supports a wildcard certificate provisioned
outside this flow.

## Runtime interfaces

- An authorized launch via `POST /api/development-workspaces/{workspaceId}/vms/{vmId}/launch`
  accepts `{ "generation": 1, "service": "t3" }`. Open the returned URL as a navigation.
  It redeems a short-lived, single-use launch code for a secure host-only cookie and redirects
  to `/`. Use the returned expiry to determine whether a fresh launch is needed. The API
  checks organization authority, user/API key validity, generation, route expiry and runtime health.
  The gateway reauthorizes active sockets and ends streams when their route or bearer changes.
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
  The gateway exposes low-cardinality .NET metrics through the `Workspaces.Gateway` meter.

## Build and verify locally

.NET 10, Python 3.11+, Git and Node.js are sufficient for the default synthetic suite.
The browser fixture also needs OpenSSL and Chromium's Linux shared libraries.

```sh
dotnet build src/Workspaces.Worker
dotnet build src/Workspaces.Gateway
dotnet test Workspaces.slnx --results-directory .local/artifacts/test-results
templates/development-workspaces/test.sh
python3 -B templates/development-workspaces/image/test-t3-candidate.py

dotnet publish src/Workspaces.Worker -c Release -o .local/artifacts/publish/worker
dotnet publish src/Workspaces.Gateway -c Release -o .local/artifacts/publish/gateway
```

The PostgreSQL matrix is optional. Set `WORKSPACES_TEST_POSTGRES_PORT` to the TCP port
of a **disposable loopback** PostgreSQL instance with trust authentication for the test
superuser `postgres`. The suite creates, migrates and drops isolated test databases.
SQLite tests exercise the Workspaces initial migration and run by default.

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
dotnet build src/Workspaces.Gateway
node scripts/workspace-browser-acceptance.mjs
```

The synthetic suite covers provisioning, enrollment, relay, persistence, deletion, Git recovery
and guest protocols. The prepared-image documentation describes the separate native image
qualification, including real mounts, systemd, T3 and provider network behavior.
