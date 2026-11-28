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

To seed from an unsigned local build instead, build a clean bundle with
[`build-release.sh`](../scripts/delivery/README.md) and replace `--release` with
`--bundle-dir <dir> --trust local-build`. Plan requires exactly one of the two.
It verifies the bundle's checksum inventory, archive component hashes and image
archives, requires `sourceDirty: false` and the CLI's own source revision, and
labels the plan as an unsigned local build. Apply and resume re-verify the same
directory and refuse a changed bundle. The host loads and checks the bundle's
image archives before starting Compose and records trust `local-build`, which
is also reported by `/health/version`. Cosign is not needed for this path.

Plan also decides whether to reuse the same provider account for coding VMs in
the first organization. The default is yes: an interactive terminal asks
`[Y/n]`, unattended plans choose yes, and `--workspace-provider no` opts out.
On yes, plan also quotes a default workspace plan: the cheapest available x64
size in the seed region with 8 GiB of memory and at least 2 vCPUs (for example
Vultr `vc2-4c-8gb`), on the provider's stock Ubuntu 24.04 image. Only the
choice and the quote are journaled. Apply or resume then:

- writes a default `DevelopmentWorkspaces` policy into the initial host policy:
  the `standard` profile, the `ubuntu-24-04` stock image (`Source: ubuntu`),
  provider pricing and a fixed volume rate (Hetzner's published net rate;
  0.10 USD/GB-month for DigitalOcean and Vultr). The installer stamps
  `PricingUpdatedAt` and `QualifiedAt` with the host clock and sets
  `MaxPricingAgeDays` to 365; review prices under `DevelopmentWorkspaces`.
- re-reads `--credential-file` and stages it over the pinned SSH connection's
  stdin into an owner-only file on a Compose-managed volume
  (`api-bootstrap stage-provider`).

If the first-owner token expires after seed completes, run
`municode seed rotate-setup-token <operation>` with the same CLI configuration
directory. It uses the pinned SSH host and replaces the protected local token
file. An interactive terminal prints the new one-time setup link.

Use `municode seed clean-staging <operation>` after seed completes to remove
the installer upload and extraction from the host before an in-place update.

After first-owner setup completes, the API validates the credential with the
provider, stores it through the normal OpenBao provider-account path as
`Default provider` (`provider_seed`), creates `Default workspace plan`
(`plan_seed`) for the quoted size and region, records `provider.created` and
`plan.created` audit events from `system:seed` and deletes the staged file. A
coding VM can be launched immediately: the worker provisions stock Ubuntu over
SSH with the pinned T3, Codex, Claude, Gemini and GitHub Copilot CLIs (about
15 minutes), so no builder VPS or snapshot is needed. A prepared image is
optional and only speeds later launches. Retries never create a second account
of the same type. A rejected credential is discarded and logged without its
value. Releases without `stage-provider` or `--workspace-defaults` print a
warning or skip the defaults and continue. Seed itself creates no coding VM.

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
`<config-home>/seed/<operation>/setup-token` with owner-only permissions. In an
interactive terminal, seed prints a setup link,
`https://console.example.com/setup#token=<token>`. The token is in the URL
fragment, which browsers never send to the server; the setup page reads it,
removes it from the address bar and history entry, and prefills the form. Treat
the link like a password: it works once. Redirected or unattended output never
contains the token or link, and the journal never stores them; open
`https://console.example.com/setup` and enter the token from the file instead.
Manual entry always remains available.

If you opted out of provider reuse, register a provider, image and plan
explicitly; use a test plan to run [image release acceptance](setup/runtime.md#release-a-workspace-image)
before admitting a prepared image. The seed token remains on the operator
workstation. The provider credential is copied to OpenBao only if reuse is on.
Seed also sets `DevelopmentWorkspaceWorker:ManagementAddresses` in the initial
host policy to the VPS's public IPv4 `/32`. No coding VM is created by this
flow.

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

Before installing, create `/etc/municode/config/operator.env` as root. Use your
public names and one stable installation ID:

```dotenv
MUNICODE_INSTALLATION_ID=replace-with-stable-installation-id
CONSOLE_HOSTNAME=console.example.test
CONSOLE_URL=https://console.example.test
API_HOSTNAME=api.example.test
API_URL=https://api.example.test
MUNICODE_GATEWAY_DOMAIN=workspaces.example.test
```

```sh
sha256sum --check --strict checksums.txt
bundle_sha256="$(sha256sum municode-X.Y.Z-linux-any.tar.gz | cut -d ' ' -f 1)"
tar -xzf municode-X.Y.Z-linux-any.tar.gz
cd municode-X.Y.Z
sudo ./install.sh --bundle-sha256 "$bundle_sha256"
```

The installer rejects symlink payloads, verifies component digests and generates
protected database, gateway and Kratos secrets, an OpenBao service certificate
and a dedicated worker SSH key. Protect and back up `/etc/municode/secrets`;
do not put it in the release tree. Reinstalling the exact active bundle is a
no-op; use `municode deploy` to change releases.

The read-only tree is `/opt/municode/releases/<release-id>`, with
`/opt/municode/current` pointing to it. `/var/lib/municode/active-release.json`
records its identity. Manual installs default to `local-build` trust; pass
`--verified-release-tag municode-vX.Y.Z` only if you verified the checksum
inventory's Sigstore signature and exact Municode release workflow identity.
Seed always verifies that identity before any provider mutation.

The release bundle ships a host-side `deploy_host.py` for the deploy CLI. It
stages a verified archive without switching `current` or restarting services:

```sh
sudo python3 -B deploy_host.py stage --operation <op> \
  --bundle <uploaded-archive> --bundle-sha256 <sha256> \
  --trust github-sigstore --verified-release-tag <verified-tag>
sudo python3 -B deploy_host.py preflight --plan <plan.json> --json
```

For a clean local bundle with image archives, use `--trust local-build` and
omit `--verified-release-tag`. The preflight plan supplies the staged
`targetRelease` and `deployProtocol`, plus `operationId`, `instanceName`,
`installationId` and the pinned SSH `hostFingerprint`. Preflight reports
blockers and approval inputs as JSON without modifying the running
installation. Missing on-host OpenBao unseal material is reported separately:
it must be supplied at apply time, not copied into the plan.

## Configure and initialize

The installer renders the release's read-only `.env` from the exact release
values, generated installer values, and `operator.env`. Do not edit `.env`.
Stage operator environment changes in `operator.env` for the next deploy. If a
reverse proxy or firewall imposes another private address, retain the API, gateway
and worker consistency described in [runtime setup](setup/runtime.md).

Workspace policy lives at `/etc/municode/config/municode.json`, outside the
release tree. If no policy exists, the installer copies the bundled template,
or copies the input file supplied with `--policy-file`, into that location.
With the bundled template, `--management-address <ipv4>/32` (or an IPv6 `/128`)
fills an empty `DevelopmentWorkspaceWorker:ManagementAddresses`. An existing
policy is never rewritten.

Passkey sign-in is enabled by default. Kratos uses `CONSOLE_HOSTNAME` as the
WebAuthn relying-party ID and `CONSOLE_URL` as its only allowed origin, so
`CONSOLE_HOSTNAME` must be the bare host name of the HTTPS `CONSOLE_URL`.
Passkeys are bound to that host name: changing the dashboard domain later
invalidates every enrolled passkey, and users must sign in with their password
and enroll again. Set `MUNICODE_PASSKEYS_ENABLED=false` in `operator.env` to turn
passkeys off for both Kratos and the dashboard; password sign-in remains
available either way.

Use `/usr/local/sbin/municode-compose` as root for manual Compose operations.
It resolves the active release and uses the fixed Compose project `municode`.
Start only PostgreSQL and OpenBao first:

```sh
municode-compose up -d postgres
municode-compose up -d openbao
```

Initialize and unseal OpenBao with your approved offline ceremony. This manual
installation path does not write OpenBao's unseal material or root token into the
installation (unlike the protected recovery record created by seed). From a
protected temporary root-token file, configure the Municode
mount and AppRole:

```sh
OPENBAO_ROOT_TOKEN_FILE=/protected/root-token \
  municode-compose --profile maintenance run --rm openbao-configure
```

The root token can then be removed. Restart OpenBao after a host reboot, supply
the configured unseal threshold and confirm the `openbao-agent` health check.
The agent renews the service token and exposes it only through a memory-backed
Compose volume.

## Migrate, bootstrap and start

Run the database jobs and start the remaining services:

```sh
municode-compose up database-initialize kratos-migrate api-migrate
municode-compose up -d
curl --fail https://api.example.test/health
```

Create a one-time first-owner request using the `api-bootstrap` maintenance
service and a protected file under the secrets directory's `bootstrap` folder.
Use the control-plane [bootstrap commands](setup/control-plane.md), substituting
the Compose service invocation:

```sh
printf '%s\n' '{"installationId":"replace-with-installation-id","operationId":"first-operator"}' |
  municode-compose --profile maintenance run --rm --no-TTY api-bootstrap \
    prepare --stdin --output-file /run/municode-bootstrap/setup-token
```

Complete setup over the public API from the console origin. Remove setup input,
output and the one-time token immediately after success. Test the private candidate with
an isolated provider registration and plan before enabling user workspace admission. See
[control-plane policy](setup/control-plane.md) and [runtime setup](setup/runtime.md).

## Register a control plane

Register any deploy-capable control plane reachable over SSH as root or as a
user with passwordless `sudo -n`. Use a host, `user@host`, `host:port`, or an
SSH config alias. Verify the SHA256 fingerprint independently before confirming
it; non-interactive use requires the flag.

```sh
municode instance add production --ssh ops@control.example.com \
  --confirm-host-fingerprint "$verified_fingerprint"
municode instance list
municode instance show production
municode instance remove production
```

All four commands accept `--json`. `show` makes a live read-only SSH probe;
`list` shows local registrations and `remove` never changes the host. The
registry stores the pinned public host key and, if given, the path from
`--identity-file`, not the private key. Seed registers automatically using its
protected journal key. To override the default name (the dashboard hostname),
pass `--instance-name <name>` to `municode seed plan`.

## Operate and upgrade

### Quick path: register and deploy

```sh
municode instance add production --ssh ops@control.example.com \
  --confirm-host-fingerprint SHA256:...
municode deploy plan --instance production --release latest
municode deploy apply <operation> --approve <digest>
```

This registers any deploy-capable host you can reach over SSH, including one
installed with `install.sh` rather than `seed`. Verify the host fingerprint
independently before confirming it. For a non-root SSH user, the host must allow
passwordless `sudo -n`. Review the plan and use its exact operation and approval
digest. `latest` selects the newest eligible published signed release; drafts
and prereleases are excluded unless `--prerelease` is used.

### Plan, apply and inspect

`municode deploy plan` verifies and stages the release, then runs a read-only
preflight. Check its blockers, release and configuration changes, migration
state, image set and approval digest before applying. A blocked plan has no
approval digest. For an exact tag, replace `latest` with the tag printed by the
release. To deploy a locally built release, build a clean bundle with
[`build-release.sh`](../scripts/delivery/README.md), then plan with
`--bundle-dir <dir> --trust local-build`; this unsigned trust level is recorded
on the host and in `/health/version`.

Apply runs in a host systemd unit, drains nonterminal workspace operations
(15 minutes by default), backs up the matched database and host state, migrates,
starts the target and checks public health endpoints. `--drain-timeout
<seconds>` changes the drain limit. `--offhost-backup <dir>` also downloads the
matched backup to that local directory, verifies its files and keeps them
owner-only; it includes secrets, so protect the directory as carefully as the
host backup.

Use `municode deploy status <operation>` to read local and host progress, or
`municode deploy status --instance <name>` to inspect the active release and
latest host operation without a local operation journal. Status also reports
the public `/health/version` when available.

If the workstation or SSH connection is interrupted, run
`municode deploy resume <operation>`. The host journal is authoritative, and
resume follows a running unit or continues recovery. Before traffic may have
opened, a failed or interrupted apply automatically starts the previous release;
if migrations began, it restores the matched snapshot first. Create a new plan
to retry. After the host records that traffic may have opened, it does not
automatically restore user data: resume retries forward. Rolling forward with a
fixed release is the recommended recovery.

If OpenBao's unseal key is not already on the host, pass
`--unseal-key-file <path>` to apply or resume. The file must be protected and
owner-only. The CLI streams the key over pinned SSH stdin; it is not put in
arguments, the plan or journal. Supply it again if a resumed operation must
restart a unit that needs the key.

### Manual rollback

`municode deploy rollback <operation>` restarts the previous release. After
traffic may have opened, the command requires explicit acknowledgements. If the
database must be restored, `--restore-database` restores the matched snapshot
and `--accept-data-loss-since <timestamp>` must exactly match `openAt` from
`municode deploy status <operation>`. This acknowledges losing writes since
traffic opened. If the worker resumed, also pass
`--accept-provider-reconciliation`; restoring the snapshot can forget provider
resources created since it, which must be reconciled from the host backup's
`post-open-operations.json`. Pass `--unseal-key-file <path>` when the host does
not have the required key.

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
