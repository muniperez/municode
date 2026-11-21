# Municode Workspaces control plane

The .NET API owns Workspaces identity, organizations, provider accounts, plans,
workspace lifecycle, and Git access. It uses a dedicated SQLite or PostgreSQL
database. Browser identity uses a dedicated Ory Kratos instance. Provider and
GitHub App credentials use a dedicated OpenBao KV v2 mount. Hetzner, DigitalOcean,
and Vultr have workspace-specific adapters.

## Build and test

Use the .NET 10 SDK and run these commands from the repository root.

```sh
dotnet clean Workspaces.slnx -c Release
dotnet build Workspaces.slnx -c Release
dotnet test Workspaces.slnx -c Release --no-build --results-directory .local/artifacts/test-results
```

Build and test outputs go under `.local/artifacts/`. Tests use synthetic
identities, repositories, secrets and HTTP transports. To include the optional
PostgreSQL migration and session concurrency test, set
`WORKSPACES_TEST_POSTGRES` to a connection string for a fresh, disposable local
PostgreSQL database before running the suite.

## Start with a fresh database

```sh
dotnet run --project src/Workspaces.Api -c Release --no-build -- migrate
dotnet run --project src/Workspaces.Api -c Release --no-build -- --urls http://127.0.0.1:5080
```

The default database is `.local/artifacts/data/workspaces.db` under the
checkout root, or under the working directory when run outside a checkout. Check `/health/ready` on the local listener. Workspace
admission and gateway launch are disabled by default. This configuration is
sufficient to verify migration and API startup without external services.

See the [control-plane setup](docs/setup/control-plane.md) for fresh operator
bootstrap, provider policy, and the API contracts used by the runtime and clients.
See [licensing](docs/licensing.md) for the extracted source and contract notices.
