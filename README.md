# Municode Workspaces

Standalone coding-workspace control plane and delivery. The API owns identity,
organizations, provider accounts, plans, workspace lifecycle and Git access; the
worker provisions retained or ephemeral VMs; and the gateway serves authenticated
T3 and preview traffic over private SSH forwards. The dashboard and CLI are
independent client projects consumed by the release package.

This repository has its own SQLite/PostgreSQL data, Ory Kratos identity, OpenBao
credentials, worker SSH keys and Caddy proxy. Hetzner, DigitalOcean and Vultr use
workspace-specific adapters. No application-hosting installation or account is
required.

## Seed a control plane

From a released standalone CLI on an operator workstation:

```sh
chmod 600 /protected/hetzner.token
workspaces seed plan \
  --provider hetzner --region <location> --size <server-type> \
  --dashboard-hostname console.example.com \
  --api-hostname api.example.com \
  --gateway-domain workspaces.example.com \
  --release workspaces-vX.Y.Z \
  --credential-file /protected/hetzner.token
workspaces seed apply <operation> \
  --credential-file /protected/hetzner.token --approve <plan-digest>
```

Seed is a signed-release, one-control-plane-VPS workflow for Hetzner, DigitalOcean
or Vultr. It prints authoritative recurring costs and creates nothing before the
exact plan digest is approved. The durable journal safely resumes an uncertain
provider response, but pauses for an independently checked SSH console
fingerprint and exact DNS records. `workspaces seed resume <operation>` continues
preparation, bundle verification, HTTPS/health checks and first-owner setup.
`municode seed` and `municode workspaces seed` are compatibility spellings.

The bootstrap provider token is read only from a protected local file. It is
never copied into the control-plane journal, release, server or provider-account
store. Seed creates no coding VM; provider registration, image qualification and
the first workspace are separate post-login actions. See the
[operator guide](docs/operator.md) for the complete safety contract and
[live validation checklist](docs/setup/live-seed-validation.md) for the manual
test that must run on disposable provider capacity outside CI.

## Build and test

Use the .NET 10 SDK and Python 3.11+ from the repository root. All generated
output is ignored under `.local/artifacts/`.

```sh
dotnet clean Workspaces.slnx -c Release
dotnet build Workspaces.slnx -c Release
dotnet test Workspaces.slnx -c Release --no-build --results-directory .local/artifacts/test-results
templates/development-workspaces/test.sh
python3 -B templates/development-workspaces/image/test-t3-candidate.py
python3 -B -m unittest discover -s scripts/delivery -p 'test_*.py' -v
```

Tests use synthetic identities, repositories, secrets, HTTP transports and client
payloads. To include the optional PostgreSQL test, set
`WORKSPACES_TEST_POSTGRES` to a fresh disposable database.

## Package

Publishing API, worker and gateway is documented independently of client feature
code. Release CI builds the real merged dashboard static output and four
self-contained CLI targets through [package targets](packaging/targets.json).
The package handoff smoke test builds and verifies all five actual payloads.

```sh
dotnet publish src/Workspaces.Api -c Release -o .local/artifacts/publish/api
dotnet publish src/Workspaces.Worker -c Release -o .local/artifacts/publish/worker
dotnet publish src/Workspaces.Gateway -c Release -o .local/artifacts/publish/gateway
# Supply the merged dashboard build and self-contained CLI RID outputs.
python3 -B scripts/delivery/package.py \
  --version 0.0.0-local --source "$(git rev-parse HEAD)" --rid linux-any \
  --api .local/artifacts/publish/api \
  --worker .local/artifacts/publish/worker \
  --gateway .local/artifacts/publish/gateway \
  --dashboard .local/artifacts/dashboard-dist \
  --cli .local/artifacts/cli \
  --output .local/artifacts/release
```

The archive contains the Compose/Caddy bundle, API/worker/gateway payloads,
dashboard, CLI, installer, operator/user guides and a component inventory. Exact
release checksums are written under `.local/artifacts/release`.

The merged CLI and dashboard projects are built and included by delivery CI.
Their package paths and release commands are fixed in `packaging/targets.json`;
release tags fail closed when either artifact cannot be built. The package adds a
`workspaces` command alias beside `municode` for each Unix RID and `workspaces.cmd`
for Windows.

## Start with a fresh database

```sh
dotnet run --project src/Workspaces.Api -c Release --no-build -- migrate
dotnet run --project src/Workspaces.Api -c Release --no-build -- --urls http://127.0.0.1:5080
```

The default database is `.local/artifacts/data/workspaces.db`. Check `/health`
on the local listener. Workspace admission and gateway launch are disabled by
default. This configuration verifies migration and API startup without external
services.

See the [control-plane setup](docs/setup/control-plane.md) for fresh operator
bootstrap, provider policy and API contracts. Use the
[operator guide](docs/operator.md) for a packaged installation and the
[user guide](docs/user.md) for the installed product. See
[licensing](docs/licensing.md) for extracted source and contract notices.
