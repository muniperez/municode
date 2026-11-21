# Operate Municode Workspaces

Municode Workspaces is a standalone control plane for coding workspaces. Its API,
worker, gateway, dashboard, PostgreSQL, identity, secret storage and TLS proxy have
independent state and credentials. The operator does not need an application-hosting
installation, account, CLI or reachable service.

## Release prerequisites and DNS

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

## Final client integration

The package target contract is `packaging/targets.json`. Release CI builds the
merged self-contained CLI outputs from
`src/Workspaces.Cli/Workspaces.Cli.csproj`. When the dashboard child project
merges, release CI consumes its `src/Workspaces.Dashboard/dist` static output;
no packaging code assumes a client implementation. A tagged release fails closed
while the dashboard artifact is missing.
