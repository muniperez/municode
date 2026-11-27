# Operate Municode

Municode is a standalone control plane for coding workspaces. Its API,
worker, gateway, dashboard, PostgreSQL, identity, secret storage and TLS proxy have
independent state and credentials. The operator does not need an application-hosting
installation, account, CLI or reachable service.

## Seed a fresh control plane

The preferred first installation path is `municode seed`. Use the exact CLI
from an immutable `municode-vX.Y.Z` release, Cosign 2.6.5, OpenSSH, a protected
provider-token file, and public DNS names you control. Seed supports Hetzner,
DigitalOcean and Vultr. The token file must be owner-only and contain only the
single API token:

```sh
install -m 600 /dev/null /protected/provider.token
# Populate the protected file without putting its value in shell history.
municode seed plan \
  --provider hetzner --region <location> --size <server-type> \
  --dashboard-hostname console.example.com \
  --api-hostname api.example.com \
  --gateway-domain workspaces.example.net \
  --release municode-vX.Y.Z \
  --credential-file /protected/provider.token
```

Plan verifies the Municode GitHub Actions Sigstore identity, exact
release manifest, checksum inventory, archive component hashes and immutable
dependency image digests. It quotes the provider's current recurring cost and
records a new local operation under the Municode config directory. It does not
create a provider resource. Review the currency, hourly/monthly terms, taxes,
traffic and optional-service exclusions. `municode` reports an approval digest;
there is no `latest`, token-on-command-line or implicit-cost approval.

```sh
municode seed apply <operation> \
  --credential-file /protected/provider.token --approve <approval-digest>
```

Apply refreshes the quote, refuses a changed approval, writes creation intent to
the interruption-safe journal before the one create call, and adopts no resource
without the operation's provider ownership marker. A timeout or process kill
does not trigger a second create: `seed resume` inventories the account, requires
exactly one matching control-plane VPS and continues from the durable journal.
`municode seed status <operation>` is local state only and never proves live
provider health. Created resources remain billable.

After public IPv4 assignment, apply or resume intentionally pauses for independent
SSH identity confirmation and returns without running SSH host commands or
transferring secrets. This is expected, not an installation failure; the VPS
remains billable. Resume without a confirmed fingerprint repeats the pause.
Obtain the ed25519 host fingerprint only from the provider's authenticated console.

For Hetzner, use the [browser console procedure](https://docs.hetzner.com/cloud/servers/getting-started/vnc-console/):

1. Sign in to the Hetzner Console, open the project used by seed, and select the
   server. Match its server ID and public IPv4 to the values printed by seed, and
   verify its seed operation ownership marker before continuing.
2. Select the console icon at the top right of the server page to open the VNC
   console. Use the installed Ubuntu system, not a booted rescue system: rescue
   has a different SSH host identity.
3. At the login prompt, log in as `root` with the initial password supplied by
   Hetzner. If the server was created with an SSH key or the password is unavailable,
   use the server's **Rescue** tab, **Root password**, **Reset root password**;
   Hetzner displays the new password in its authenticated Console. This resets
   the installed system's root password; do not enable or boot Rescue.
4. Ubuntu may require changing the initial root password before granting a shell.
   Follow the current-password, new-password and confirmation prompts, then log
   in again if prompted. Password input is not echoed. Seed disables SSH password
   login; this password is for the provider console, not password-based SSH.
   Do not re-enable SSH password authentication to get the fingerprint.
5. In the root shell on that server, run this literal command (preserve the
   underscores in `ssh_host_ed25519_key.pub`):

```sh
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Read the exact `SHA256:...` field from the `(ED25519)` output, not the key comment
or entire output line. On the operator workstation, pass that value to resume:

```sh
municode seed resume <operation> \
  --credential-file /protected/provider.token \
  --confirm-console-fingerprint SHA256:...
```

The CLI compares the SSH key offered by the recorded VPS public IPv4 against
that independently obtained console fingerprint, then pins the matching key in
a protected `known_hosts` entry. Network-observed keys alone are not a trust
source. A changed or different candidate fails before any SSH host command or
secret transfer; stop and reconcile the server identity rather than accepting
the candidate or replacing the pin. Resume prints these
exact records and waits for them to resolve only to the new VPS:

| Record | Value |
| --- | --- |
| `console.example.com` A | control-plane public IPv4 |
| `api.example.com` A | control-plane public IPv4 |
| `*.workspaces.example.net` A | control-plane public IPv4 |

After DNS propagation, resume prepares Docker/Compose/Python, installs the exact
archive, configures Caddy and starts API, worker, gateway, dashboard, PostgreSQL,
Kratos and OpenBao. It verifies the dashboard, `/health` and setup endpoint over
trusted public HTTPS. Caddy issues the API/dashboard certificates; the wildcard
gateway certificate is acquired on demand only for valid workspace hosts after a
workspace exists.

The protected one-time owner token is downloaded to
`<config-home>/seed/<operation>/setup-token` with owner-only permissions and is
never printed. Open `https://console.example.com/setup`, enter the token manually
and complete first-owner sign-in. Then register the provider account explicitly and use a
test plan to run [image release acceptance](setup/runtime.md#release-a-workspace-image)
before admitting the image for users. The seed token remains on the
operator workstation and is not copied to OpenBao or the provider API store by
seed. No coding VM is created by this flow.

OpenBao is initialized with one protected recovery record at
`/etc/municode/secrets/openbao-seed-init.json`. Move an independent
copy of that recovery material and the other installation secrets to approved
offline custody before production use. On host restart, follow the ordinary
unseal procedure; seed never copies unseal material into the Municode
application or provider account.

An interrupted OpenBao initialization is not automatically recoverable. Resume
requires a valid readiness response: unknown or malformed status preserves the
volume and recovery records and fails. If OpenBao positively reports initialized
but the seed recovery output is missing or malformed, resume also fails without
resetting the volume or deleting the pending output. Installation ownership and
the absence of a configuration marker do not make initialized data safe to reset.
An automatic volume reset requires a positive uninitialized response, an existing
seed recovery record, no configured marker, and verified ownership of the unique
volume and its containers.

For an initialized volume, a valid `openbao-seed-init.json.pending` can be promoted
on resume without reinitialization. If the record is damaged or missing, preserve
the volume and remaining evidence and reconcile under the approved offline
recovery procedure. Only genuine recovery material verified to belong to that
same OpenBao initialization can be restored to the protected seed recovery record;
syntactically valid JSON or keys from another initialization are not recovery.
Seed cannot reconstruct lost unseal keys or a lost root token, and this path
provides no automatic reset or bypass for initialized data.

Synthetic provider, SSH and release fixtures exercise the retry paths in CI.
Do not turn those fixtures into live credentials. The explicit
[live seed validation checklist](setup/live-seed-validation.md) remains a
separate, disposable-machine operation.

## Release prerequisites and DNS

The manual-host path below remains available for operators that already own a
Linux host. It does not provide the provider/VPS journal offered by seed.

Use a Linux host with Docker Engine, Compose v2, Python 3, OpenSSL and OpenSSH.
Choose three controlled DNS names and create records that point to the host:

| Record | Purpose |
| --- | --- |
| `api.example.test` | Municode API |
| `console.example.test` | dashboard |
| `*.workspaces.example.test` | generation-bound T3 and preview origins |

The wildcard must have no competing certificate or reverse proxy. Open ports 80
and 443. Keep OpenBao, PostgreSQL, Kratos, the API's internal gateway routes and
the relay range private to Compose networks.

Download the exact `municode-vX.Y.Z` assets. Independently verify the
`checksums.txt` entries and the source revision shown by the release, then
extract the archive. Never install from a mutable `latest` URL.

```sh
sha256sum --check --strict checksums.txt
tar -xzf municode-X.Y.Z-linux-any.tar.gz
cd municode-X.Y.Z
./install.sh --install-root /opt/municode/workspaces \
  --secrets-dir /etc/municode/secrets
```

The installer refuses existing destinations, rejects symlink payloads, verifies
component digests and generates protected database, gateway and Kratos secrets,
an OpenBao service certificate and a dedicated worker SSH key. Protect and back
up `/etc/municode/secrets`; do not put it in the installation tree.
Tagged releases also carry `checksums.txt.sigstore.json`; seed requires its exact
Municode workflow identity before any provider mutation.

## Configure and initialize

Edit `/opt/municode/workspaces/.env`. Replace all `example.test` names and the
installation ID, and retain image references from the exact release metadata. If a
reverse proxy or firewall imposes another private address, retain the API,
gateway and worker consistency described in [runtime setup](setup/runtime.md).

Passkey sign-in is enabled by default. Kratos uses `CONSOLE_HOSTNAME` as the
WebAuthn relying-party ID and `CONSOLE_URL` as its only allowed origin, so
`CONSOLE_HOSTNAME` must be the bare host name of the HTTPS `CONSOLE_URL`.
Passkeys are bound to that host name: changing the dashboard domain later
invalidates every enrolled passkey, and users must sign in with their password
and enroll again. Set `MUNICODE_PASSKEYS_ENABLED=false` in `.env` to turn
passkeys off for both Kratos and the dashboard; password sign-in remains
available either way.

Start only PostgreSQL and OpenBao first:

```sh
cd /opt/municode/workspaces
docker compose up -d postgres
docker compose up -d openbao
```

Initialize and unseal OpenBao with your approved offline ceremony. This manual
installation path does not write OpenBao's unseal material or root token into the
installation (unlike the protected recovery record created by seed). From a
protected temporary root-token file, configure the Municode
mount and AppRole:

```sh
OPENBAO_ROOT_TOKEN_FILE=/protected/root-token \
  docker compose --profile maintenance run --rm openbao-configure
```

The root token can then be removed. Restart OpenBao after a host reboot, supply
the configured unseal threshold and confirm the `openbao-agent` health check.
The agent renews the service token and exposes it only through a memory-backed
Compose volume.

## Migrate, bootstrap and start

Run the database jobs and start the remaining services:

```sh
docker compose up database-initialize kratos-migrate api-migrate
docker compose up -d
curl --fail https://api.example.test/health
```

Create a one-time first-owner request using the `api-bootstrap` maintenance
service and a protected file under the secrets directory's `bootstrap` folder.
Use the control-plane [bootstrap commands](setup/control-plane.md), substituting
the Compose service invocation:

```sh
printf '%s\n' '{"installationId":"replace-with-installation-id","operationId":"first-operator"}' |
  docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
    prepare --stdin --output-file /run/municode-bootstrap/setup-token
```

Complete setup over the public API from the console origin. Remove setup input,
output and the one-time token immediately after success. Test the private candidate with
an isolated provider registration and plan before enabling user workspace admission. See
[control-plane policy](setup/control-plane.md) and [runtime setup](setup/runtime.md).

## Operate and upgrade

Back up PostgreSQL, OpenBao, Caddy state, installation state and protected secrets
together. Keep recovery material offline. Before an upgrade, download and verify
a new exact archive, stop accepted work, back up, install into a new root, and
switch only after migrations and health checks pass. Do not overwrite an active
release or reuse its installation ID on another host.

On a release that supports maintenance mode, run the following against the active
release's database before stopping the worker. Use the API executable with the
same database configuration as the running API:

```sh
Municode.Api maintenance enter
Municode.Api maintenance status --json
```

For the Compose bundle, use `docker compose run --rm --no-deps api-migrate`,
passing the DLL path from that service's `command` followed by the maintenance arguments.

Keep the worker running until `enabled` is `true` and `nonterminalOperations`
is `0`; operations can be pending without an active lease. If the drain times out,
do not stop the worker. Use `maintenance exit` to reopen admission after an aborted
drain or once the new release and its worker are running. Reads and existing
workspace connections remain available while only maintenance mode is active.

The worker evaluates budgets and delivers budget webhooks. Tune it with the
`BudgetAlerts` configuration section: `Enabled` (default `true`),
`EvaluationIntervalSeconds` (300), `DeliveryPollSeconds` (30),
`MaxDeliveryAttempts` (6), `RetryBaseSeconds` (60), `RetryMaxSeconds` (3600),
`WebhookTimeoutSeconds` (10) and `DeliveryBatchSize` (25). Webhook targets must
resolve to public addresses. `AllowPrivateWebhookTargets` relaxes the worker's
resolved-address check for isolated test installations only; the API still
rejects private and loopback literals. Webhook signing secrets live in the secret
store, never in PostgreSQL.

Budget email is off until you configure SMTP in `BudgetAlerts:Email` for both
the API and the worker. The API uses it to report whether email is available.
The keys are `Enabled`, `Host`, `Port` (587), `FromAddress`, `FromName`
(`Municode`), `Username`, `PasswordSecretPath`, `TimeoutSeconds` (30) and
`MaxRecipientsPerBudget` (50).

The worker always requires STARTTLS; implicit TLS on port 465 is not supported.
Keep the SMTP password in OpenBao, never in configuration. Store it as the
`value` field of a KV secret, for example
`bao kv put -mount=municode env/<prefix>/operator/budget-smtp value=...`. Then
set `PasswordSecretPath` to `env/<prefix>/operator/budget-smtp`. The bundled
service policy grants read-only access to `env/+/operator/+`. Omit `Username` for unauthenticated
relays. If email is disabled or incomplete, the worker queues no email and
cancels already queued email. SMTP failures follow the same retry settings as
webhooks and record only the SMTP status code.

The worker also records provider-reported month-to-date charges for provider
accounts in organizations that have at least one budget. It calls only
read-only billing endpoints with each account's existing credential and stores
only the aggregated amount, currency, source and retrieval time. Tune it with
`BudgetAlerts:ProviderCharges`: `Enabled` (default `true`), `RefreshMinutes`
(60, at least 15) and `StaleAfterMinutes` (180). Set `StaleAfterMinutes` for the
API too; the API uses it to mark old amounts as stale. Failed refreshes record
only an HTTP status code or a fixed description, never the provider's response.
They are retried at the next refresh interval and never affect resources.
DigitalOcean tokens need the `billing:read` scope, and Vultr API keys need
billing access; without them the budget card shows the refresh as failed.

For a failed startup, inspect only service status and sanitized logs; bearer
capabilities can occur in gateway requests. Do not paste request URLs, tokens,
OpenBao values, bootstrap material or worker private keys into support output.

## Client integration

The package target contract is `packaging/targets.json`. Release CI builds the
merged self-contained CLI outputs from
`src/Municode.Cli/Municode.Cli.csproj` and the merged dashboard static output
from `modules/dashboard`, which Vite writes to
`.local/artifacts/dashboard-dist`. No packaging code implements client behavior.
A tagged release fails closed when either client artifact is missing.
