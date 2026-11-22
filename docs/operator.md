# Operate Municode Workspaces

Municode Workspaces is a standalone control plane for coding workspaces. Its API,
worker, gateway, dashboard, PostgreSQL, identity, secret storage and TLS proxy have
independent state and credentials. The operator does not need an application-hosting
installation, account, CLI or reachable service.

## Seed a fresh control plane

The preferred first installation path is `workspaces seed`. Use the exact CLI
from an immutable `workspaces-vX.Y.Z` release, Cosign 2.6.5, OpenSSH, a protected
provider-token file, and public DNS names you control. Seed supports Hetzner,
DigitalOcean and Vultr. The token file must be owner-only and contain only the
single API token:

```sh
install -m 600 /dev/null /protected/provider.token
# Populate the protected file without putting its value in shell history.
workspaces seed plan \
  --provider hetzner --region <location> --size <server-type> \
  --dashboard-hostname console.example.com \
  --api-hostname api.example.com \
  --gateway-domain workspaces.example.net \
  --release workspaces-vX.Y.Z \
  --credential-file /protected/provider.token
```

Plan verifies the Municode Workspaces GitHub Actions Sigstore identity, exact
release manifest, checksum inventory, archive component hashes and immutable
dependency image digests. It quotes the provider's current recurring cost and
records a new local operation under the Municode config directory. It does not
create a provider resource. Review the currency, hourly/monthly terms, taxes,
traffic and optional-service exclusions. `workspaces` reports an approval digest;
there is no `latest`, token-on-command-line or implicit-cost approval.

```sh
workspaces seed apply <operation> \
  --credential-file /protected/provider.token --approve <approval-digest>
```

Apply refreshes the quote, refuses a changed approval, writes creation intent to
the interruption-safe journal before the one create call, and adopts no resource
without the operation's provider ownership marker. A timeout or process kill
does not trigger a second create: `seed resume` inventories the account, requires
exactly one matching control-plane VPS and continues from the durable journal.
`workspaces seed status <operation>` is local state only and never proves live
provider health. Created resources remain billable.

After public IPv4 assignment, obtain the ed25519 host fingerprint only from the
provider's authenticated console and compare its exact `SHA256:...` value:

```sh
workspaces seed resume <operation> \
  --credential-file /protected/provider.token \
  --confirm-console-fingerprint SHA256:...
```

The CLI pins that key in a protected `known_hosts` entry. A changed or different
candidate fails before any SSH command or secret transfer. Resume prints these
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
and complete first-owner sign-in. Then register the provider account explicitly,
qualify a workspace image and admit a plan. The seed token remains on the
operator workstation and is not copied to OpenBao or the provider API store by
seed. No coding VM is created by this flow.

`municode seed` and `municode workspaces seed` are compatibility aliases. The
release package's `workspaces` wrapper is the canonical spelling.

OpenBao is initialized with one protected recovery record at
`/etc/municode/workspaces/secrets/openbao-seed-init.json`. Move an independent
copy of that recovery material and the other installation secrets to approved
offline custody before production use. On host restart, follow the ordinary
unseal procedure; seed never copies unseal material into the Workspaces
application or provider account.

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
| `api.example.test` | Workspaces API |
| `console.example.test` | dashboard |
| `*.workspaces.example.test` | generation-bound T3 and preview origins |

The wildcard must have no competing certificate or reverse proxy. Open ports 80
and 443. Keep OpenBao, PostgreSQL, Kratos, the API's internal gateway routes and
the relay range private to Compose networks.

Download the exact `workspaces-vX.Y.Z` assets. Independently verify the
`checksums.txt` entries and the source revision shown by the release, then
extract the archive. Never install from a mutable `latest` URL.

```sh
sha256sum --check --strict checksums.txt
tar -xzf municode-workspaces-X.Y.Z-linux-any.tar.gz
cd municode-workspaces-X.Y.Z
./install.sh --install-root /opt/municode/workspaces \
  --secrets-dir /etc/municode/workspaces/secrets
```

The installer refuses existing destinations, rejects symlink payloads, verifies
component digests and generates protected database, gateway and Kratos secrets,
an OpenBao service certificate and a dedicated worker SSH key. Protect and back
up `/etc/municode/workspaces/secrets`; do not put it in the installation tree.
Tagged releases also carry `checksums.txt.sigstore.json`; seed requires its exact
Municode Workspaces workflow identity before any provider mutation.

## Configure and initialize

Edit `/opt/municode/workspaces/.env`. Replace all `example.test` names and the
installation ID, and retain image references from the exact release metadata. If a
reverse proxy or firewall imposes another private address, retain the API,
gateway and worker consistency described in [runtime setup](setup/runtime.md).

Start only PostgreSQL and OpenBao first:

```sh
cd /opt/municode/workspaces
docker compose up -d postgres
docker compose up -d openbao
```

Initialize and unseal OpenBao with your approved offline ceremony. OpenBao's
unseal material and root token are never written into the installation by this
bundle. From a protected temporary root-token file, configure the Workspaces
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
    prepare --stdin --output-file /run/workspaces-bootstrap/setup-token
```

Complete setup over the public API from the console origin. Remove setup input,
output and the one-time token immediately after success. Then register a provider,
qualified image and plan before enabling workspace admission. See
[control-plane policy](setup/control-plane.md) and [runtime setup](setup/runtime.md).

## Operate and upgrade

Back up PostgreSQL, OpenBao, Caddy state, installation state and protected secrets
together. Keep recovery material offline. Before an upgrade, download and verify
a new exact archive, stop accepted work, back up, install into a new root, and
switch only after migrations and health checks pass. Do not overwrite an active
release or reuse its installation ID on another host.

For a failed startup, inspect only service status and sanitized logs; bearer
capabilities can occur in gateway requests. Do not paste request URLs, tokens,
OpenBao values, bootstrap material or worker private keys into support output.

## Client integration

The package target contract is `packaging/targets.json`. Release CI builds the
merged self-contained CLI outputs from
`src/Workspaces.Cli/Workspaces.Cli.csproj` and the merged dashboard static output
from `modules/dashboard`, which Vite writes to
`.local/artifacts/dashboard-dist`. No packaging code implements client behavior.
A tagged release fails closed when either client artifact is missing.
